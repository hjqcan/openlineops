using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class StationControllerHandshakeApiTests :
    IClassFixture<OpenLineOpsApiWebApplicationFactory>,
    IDisposable
{
    private const string AgentInstanceId =
        "11111111-1111-4111-8111-111111111111";
    private const string LeaseHandle =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly WebApplicationFactory<Program> _factory;

    public StationControllerHandshakeApiTests(
        OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenLineOps:Runtime:Persistence:Provider"] =
                        RuntimeSessionPersistenceProviders.InMemory,
                    ["OpenLineOps:Runtime:Coordination:Provider"] =
                        ProductionCoordinationPersistenceProviders.InMemory,
                    ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                    ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess",
                    ["OpenLineOps:Runtime:StationControllerHandshake:TimeToLive"] =
                        "00:00:30",
                    ["OpenLineOps:Runtime:StationControllerHandshake:MaximumSourceClockSkew"] =
                        "00:00:10"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStationRecipeStartAuthority>();
                services.AddSingleton<IStationRecipeStartAuthority,
                    TestStationRecipeStartAuthority>();
            });
        });
    }

    [Fact]
    public async Task ApiFencesSequencesSessionRecoveryAndStationIdentity()
    {
        const string stationId = ApiTestAuthentication.StationAgentStationId;
        using var engineering = Client(ApiTestAuthentication.EngineeringToken);
        using var operatorClient = Client(ApiTestAuthentication.OperatorToken);
        using var safety = Client(ApiTestAuthentication.SafetyToken);
        using var agent = Client(ApiTestAuthentication.StationAgentToken);
        agent.DefaultRequestHeaders.Add(
            "X-OpenLineOps-Agent-Lease",
            LeaseHandle);

        Assert.Equal(
            HttpStatusCode.Created,
            (await engineering.PutAsJsonAsync(
                LifecycleRoute(stationId),
                new
                {
                    mode = "Automatic",
                    readiness = new
                    {
                        interlocksSatisfied = true,
                        homed = true,
                        criticalDevicesHealthy = true,
                        recipeVerified = true,
                        calibrationValid = true,
                        safetyPermitGranted = true
                    },
                    reason = "enroll station"
                })).StatusCode);
        var agentFencingToken = await AcquireLeaseAsync(agent, stationId);
        using var missing = await operatorClient.PostAsJsonAsync(
            $"{LifecycleRoute(stationId)}/commands/reset",
            new { reason = "reset without handshake" });
        using var missingJson = await ReadJsonAsync(missing);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal(
            "Conflict.Runtime.StationControllerHandshakeNotReported",
            missingJson.RootElement.GetProperty("title").GetString());

        var sourceTimestampUtc = DateTimeOffset.UtcNow;
        var firstBody = HandshakeBody(
            "controller-a",
            heartbeatSequence: 1,
            sourceTimestampUtc,
            reason: "controller online");
        using var first = await agent.PostAsJsonAsync(Route(stationId), firstBody);
        using var firstJson = await ReadJsonAsync(first);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(0, firstJson.RootElement.GetProperty("revision").GetInt64());
        Assert.True(firstJson.RootElement.GetProperty("executionAllowed").GetBoolean());

        using var replay = await agent.PostAsJsonAsync(Route(stationId), firstBody);
        using var replayJson = await ReadJsonAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(0, replayJson.RootElement.GetProperty("revision").GetInt64());

        using var conflict = await agent.PostAsJsonAsync(
            Route(stationId),
            HandshakeBody(
                "controller-a",
                heartbeatSequence: 1,
                sourceTimestampUtc,
                reason: "same sequence changed payload",
                commandSequence: 1,
                commandId: "unissued-command",
                commandFencingToken: 1));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var resetting = await operatorClient.PostAsJsonAsync(
            $"{LifecycleRoute(stationId)}/commands/reset",
            new { reason = "reset station" });
        using var resettingJson = await ReadJsonAsync(resetting);
        Assert.Equal(HttpStatusCode.OK, resetting.StatusCode);
        var resetCommand = resettingJson.RootElement
            .GetProperty("pendingControllerCommand");
        var resetSequence = resetCommand
            .GetProperty("expectedCommandSequence")
            .GetInt64();
        using var claimedReset = await agent.GetAsync(
            $"{LifecycleRoute(stationId)}/controller-command"
            + $"?ownerInstanceId={AgentInstanceId}"
            + $"&fencingToken={agentFencingToken}");
        Assert.Equal(HttpStatusCode.OK, claimedReset.StatusCode);
        using var resetCompleted = await agent.PostAsJsonAsync(
            Route(stationId),
            HandshakeBody(
                "controller-a",
                heartbeatSequence: 2,
                DateTimeOffset.UtcNow,
                reason: "reset controller command complete",
                commandSequence: resetSequence,
                acknowledgedCommandSequence: resetSequence,
                completed: true,
                commandId: resetCommand.GetProperty("commandId").GetString(),
                commandFencingToken: resetCommand
                    .GetProperty("fencingToken")
                    .GetInt64(),
                observedState: "Idle"));
        Assert.Equal(HttpStatusCode.OK, resetCompleted.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await agent.PostAsJsonAsync(
                $"{LifecycleRoute(stationId)}/commands/acknowledge",
                new
                {
                    controllerSessionId = "controller-a",
                    ownerInstanceId = AgentInstanceId,
                    fencingToken = agentFencingToken,
                    commandId = resetCommand.GetProperty("commandId").GetString(),
                    commandSequence = resetSequence,
                    observedMode = "Automatic",
                    observedState = "Idle",
                    stateSequence = 2,
                    reason = "reset complete"
                })).StatusCode);

        using var changedSession = await agent.PostAsJsonAsync(
            Route(stationId),
            HandshakeBody(
                "controller-b",
                heartbeatSequence: 1,
                DateTimeOffset.UtcNow,
                reason: "controller restarted",
                observedState: "Idle"));
        using var changedJson = await ReadJsonAsync(changedSession);
        Assert.Equal(HttpStatusCode.OK, changedSession.StatusCode);
        Assert.True(changedJson.RootElement.GetProperty("recoveryRequired").GetBoolean());
        Assert.False(changedJson.RootElement.GetProperty("executionAllowed").GetBoolean());
        var recoveryEpoch = changedJson.RootElement
            .GetProperty("recoveryEpoch")
            .GetInt64();
        var operationalStateSha256 = changedJson.RootElement
            .GetProperty("operationalStateSha256")
            .GetString();

        using var heartbeatOnly = await agent.PostAsJsonAsync(
            Route(stationId),
            HandshakeBody(
                "controller-b",
                heartbeatSequence: 2,
                DateTimeOffset.UtcNow,
                reason: "recovery review heartbeat",
                observedState: "Idle"));
        Assert.Equal(HttpStatusCode.OK, heartbeatOnly.StatusCode);

        using var recoveryBlocked = await operatorClient.PostAsJsonAsync(
            $"{LifecycleRoute(stationId)}/commands/start",
            new { reason = "start before recovery acknowledgement" });
        Assert.Equal(HttpStatusCode.Conflict, recoveryBlocked.StatusCode);

        using var acknowledged = await safety.PostAsJsonAsync(
            $"{Route(stationId)}/recovery/acknowledge",
            new
            {
                expectedRecoveryEpoch = recoveryEpoch,
                controllerSessionId = "controller-b",
                operationalStateSha256,
                reason = "physical state verified"
            });
        using var acknowledgedJson = await ReadJsonAsync(acknowledged);
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        Assert.False(
            acknowledgedJson.RootElement.GetProperty("recoveryRequired").GetBoolean());
        Assert.True(
            acknowledgedJson.RootElement.GetProperty("executionAllowed").GetBoolean());

        using var started = await operatorClient.PostAsJsonAsync(
            $"{LifecycleRoute(stationId)}/commands/start",
            new { reason = "start after recovery acknowledgement" });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        using var facts = await operatorClient.GetAsync($"{Route(stationId)}/facts");
        using var factsJson = await ReadJsonAsync(facts);
        Assert.Equal(HttpStatusCode.OK, facts.StatusCode);
        var factItems = factsJson.RootElement.GetProperty("facts")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(4, factItems.Length);
        Assert.Equal(
            [
                "Reported",
                "Reported",
                "ControllerSessionChanged",
                "RecoveryAcknowledged"
            ],
            factItems.Select(
                static item => item.GetProperty("kind").GetString()));
        Assert.Equal(
            ApiTestAuthentication.SafetyActorId,
            factItems[^1].GetProperty("actorId").GetString());

        using var wrongStation = await agent.PostAsJsonAsync(
            Route("station.other"),
            HandshakeBody(
                "controller-other",
                heartbeatSequence: 1,
                DateTimeOffset.UtcNow,
                reason: "wrong station"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongStation.StatusCode);
        using var operatorReport = await operatorClient.PostAsJsonAsync(
            Route(stationId),
            HandshakeBody(
                "controller-a",
                heartbeatSequence: 2,
                DateTimeOffset.UtcNow,
                reason: "operator cannot impersonate agent"));
        Assert.Equal(HttpStatusCode.Forbidden, operatorReport.StatusCode);
    }

    [Fact]
    public async Task ApiRejectsUnknownFieldsAndInvalidControllerState()
    {
        const string stationId = ApiTestAuthentication.StationAgentStationId;
        using var engineering = Client(ApiTestAuthentication.EngineeringToken);
        using var agent = Client(ApiTestAuthentication.StationAgentToken);
        agent.DefaultRequestHeaders.Add(
            "X-OpenLineOps-Agent-Lease",
            LeaseHandle);
        Assert.Equal(
            HttpStatusCode.Created,
            (await engineering.PutAsJsonAsync(
                LifecycleRoute(stationId),
                new
                {
                    mode = "Automatic",
                    readiness = new
                    {
                        interlocksSatisfied = false,
                        homed = false,
                        criticalDevicesHealthy = false,
                        recipeVerified = false,
                        calibrationValid = false,
                        safetyPermitGranted = false
                    },
                    reason = "enroll validation station"
                })).StatusCode);
        _ = await AcquireLeaseAsync(agent, stationId);

        using var unknown = await agent.PostAsJsonAsync(
            Route(stationId),
            new
            {
                ownerInstanceId = AgentInstanceId,
                agentFencingToken = 1,
                controllerSessionId = "controller-a",
                heartbeatSequence = 1,
                commandSequence = 0,
                acknowledgedCommandSequence = 0,
                commandId = (string?)null,
                commandFencingToken = 0,
                observedMode = "Automatic",
                observedState = "Stopped",
                stateSequence = 1,
                busy = false,
                completed = false,
                error = false,
                errorCode = (string?)null,
                recipeConfirmed = true,
                confirmedRecipeId = "recipe-a",
                confirmedRecipeVersion = "1",
                safetyPermitGranted = true,
                sourceTimestampUtc = DateTimeOffset.UtcNow,
                reason = "unknown field",
                unexpected = true
            });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var invalid = await agent.PostAsJsonAsync(
            Route(stationId),
            new
            {
                ownerInstanceId = AgentInstanceId,
                agentFencingToken = 1,
                controllerSessionId = "controller-a",
                heartbeatSequence = 1,
                commandSequence = 1,
                acknowledgedCommandSequence = 1,
                commandId = "invalid-command",
                commandFencingToken = 1,
                observedMode = "Automatic",
                observedState = "Stopped",
                stateSequence = 1,
                busy = true,
                completed = true,
                error = false,
                errorCode = (string?)null,
                recipeConfirmed = true,
                confirmedRecipeId = "recipe-a",
                confirmedRecipeVersion = "1",
                safetyPermitGranted = true,
                sourceTimestampUtc = DateTimeOffset.UtcNow,
                reason = "invalid controller state"
            });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(string token) =>
        _factory.CreateAuthenticatedClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false },
            token);

    private static string LifecycleRoute(string stationId) =>
        $"/api/stations/{stationId}/lifecycle";

    private static string Route(string stationId) =>
        $"{LifecycleRoute(stationId)}/controller-handshake";

    private sealed class TestStationRecipeStartAuthority
        : IStationRecipeStartAuthority
    {
        public ValueTask<StationRecipeStartAuthorityDecision> ResolveAsync(
            StationId stationId,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                StationRecipeStartAuthorityDecision.Allow(
                    new StationRecipeStartAuthority(
                        "recipe-a",
                        "1",
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"),
                        Guid.Parse(
                            "22222222-2222-2222-2222-222222222222"),
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        }
    }

    private static object HandshakeBody(
        string controllerSessionId,
        long heartbeatSequence,
        DateTimeOffset sourceTimestampUtc,
        string reason,
        long commandSequence = 0,
        long acknowledgedCommandSequence = 0,
        bool completed = false,
        string? commandId = null,
        long commandFencingToken = 0,
        string observedState = "Stopped") => new
    {
        ownerInstanceId = AgentInstanceId,
        agentFencingToken = 1,
        controllerSessionId,
        heartbeatSequence,
        commandSequence,
        acknowledgedCommandSequence,
        commandId,
        commandFencingToken,
        observedMode = "Automatic",
        observedState,
        stateSequence = heartbeatSequence,
        busy = false,
        completed,
        error = false,
        errorCode = (string?)null,
        recipeConfirmed = true,
        confirmedRecipeId = "recipe-a",
        confirmedRecipeVersion = "1",
        safetyPermitGranted = true,
        sourceTimestampUtc,
        reason
    };

    private static async Task<long> AcquireLeaseAsync(
        HttpClient agent,
        string stationId)
    {
        using var response = await agent.PostAsJsonAsync(
            $"/api/stations/{stationId}/agent-control-lease/acquire",
            new { ownerInstanceId = AgentInstanceId });
        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response);
        return json.RootElement.GetProperty("fencingToken").GetInt64();
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
}
