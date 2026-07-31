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

public sealed class StationLifecycleApiTests :
    IClassFixture<OpenLineOpsApiWebApplicationFactory>,
    IDisposable
{
    private const string AgentInstanceId =
        "11111111-1111-4111-8111-111111111111";
    private const string LeaseHandle =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly WebApplicationFactory<Program> _factory;

    public StationLifecycleApiTests(OpenLineOpsApiWebApplicationFactory factory)
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
                    ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess"
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
    public async Task LifecycleApiPersistsPackMlTransitionsAndSafetyPermitAbort()
    {
        const string stationId = ApiTestAuthentication.StationAgentStationId;
        using var engineering = Client(ApiTestAuthentication.EngineeringToken);
        using var operatorClient = Client(ApiTestAuthentication.OperatorToken);
        using var agent = Client(ApiTestAuthentication.StationAgentToken);
        agent.DefaultRequestHeaders.Add(
            "X-OpenLineOps-Agent-Lease",
            LeaseHandle);

        using var created = await engineering.PutAsJsonAsync(
            Route(stationId),
            CreateBody(ready: true, reason: "commission test station"));
        using var createdJson = await ReadJsonAsync(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(Route(stationId), created.Headers.Location?.OriginalString);
        Assert.Equal(0, createdJson.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal("Stopped", createdJson.RootElement.GetProperty("state").GetString());
        var agentFencingToken = await AcquireLeaseAsync(agent, stationId);

        using var controllerReady = await agent.PostAsJsonAsync(
            $"{Route(stationId)}/controller-handshake",
            ControllerHandshakeBody(
                "controller.lifecycle",
                heartbeatSequence: 1,
                commandSequence: 0,
                acknowledgedCommandSequence: 0,
                completed: false,
                commandId: null,
                commandFencingToken: 0,
                observedState: "Stopped",
                reason: "controller ready"));
        Assert.Equal(HttpStatusCode.OK, controllerReady.StatusCode);

        using var resetting = await operatorClient.PostAsJsonAsync(
            CommandRoute(stationId, "reset"),
            new { reason = "prepare station" });
        using var resettingJson = await ReadJsonAsync(resetting);
        Assert.Equal(HttpStatusCode.OK, resetting.StatusCode);
        Assert.Equal("Resetting", resettingJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, resettingJson.RootElement.GetProperty("revision").GetInt64());
        var resetCommand = resettingJson.RootElement
            .GetProperty("pendingControllerCommand");
        var resetSequence = resetCommand
            .GetProperty("expectedCommandSequence")
            .GetInt64();
        var resetCommandId = resetCommand.GetProperty("commandId").GetString();
        var resetFencingToken = resetCommand
            .GetProperty("fencingToken")
            .GetInt64();

        using var polledReset = await agent.GetAsync(
            $"{Route(stationId)}/controller-command"
            + $"?ownerInstanceId={AgentInstanceId}"
            + $"&fencingToken={agentFencingToken}");
        using var polledResetJson = await ReadJsonAsync(polledReset);
        Assert.Equal(HttpStatusCode.OK, polledReset.StatusCode);
        Assert.Equal(
            resetCommand.GetProperty("commandId").GetString(),
            polledResetJson.RootElement
                .GetProperty("command")
                .GetProperty("commandId")
                .GetString());

        using var resetCompleted = await agent.PostAsJsonAsync(
            $"{Route(stationId)}/controller-handshake",
            ControllerHandshakeBody(
                "controller.lifecycle",
                heartbeatSequence: 2,
                commandSequence: resetSequence,
                acknowledgedCommandSequence: resetSequence,
                completed: true,
                commandId: resetCommandId,
                commandFencingToken: resetFencingToken,
                observedState: "Idle",
                reason: "reset controller command complete"));
        Assert.Equal(HttpStatusCode.OK, resetCompleted.StatusCode);

        using var idle = await agent.PostAsJsonAsync(
            CommandRoute(stationId, "acknowledge"),
            new
            {
                ownerInstanceId = AgentInstanceId,
                fencingToken = agentFencingToken,
                commandId = resetCommandId,
                controllerSessionId = "controller.lifecycle",
                commandSequence = resetSequence,
                observedMode = "Automatic",
                observedState = "Idle",
                stateSequence = 2,
                reason = "reset handshake complete"
            });
        using var idleJson = await ReadJsonAsync(idle);
        Assert.Equal(HttpStatusCode.OK, idle.StatusCode);
        Assert.Equal("Idle", idleJson.RootElement.GetProperty("state").GetString());

        using var starting = await operatorClient.PostAsJsonAsync(
            CommandRoute(stationId, "start"),
            new { reason = "start production cycle" });
        using var startingJson = await ReadJsonAsync(starting);
        Assert.Equal(HttpStatusCode.OK, starting.StatusCode);
        var startSequence = startingJson.RootElement
            .GetProperty("pendingControllerCommand")
            .GetProperty("expectedCommandSequence")
            .GetInt64();
        var startCommand = startingJson.RootElement
            .GetProperty("pendingControllerCommand");
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111",
            startCommand.GetProperty("recipeAssignmentId").GetGuid()
                .ToString("D"));
        Assert.Equal(
            "22222222-2222-2222-2222-222222222222",
            startCommand.GetProperty("recipeDeploymentId").GetGuid()
                .ToString("D"));
        Assert.Equal(
            64,
            startCommand.GetProperty("recipeConfigurationSha256")
                .GetString()!
                .Length);
        using var polledStart = await agent.GetAsync(
            $"{Route(stationId)}/controller-command"
            + $"?ownerInstanceId={AgentInstanceId}"
            + $"&fencingToken={agentFencingToken}");
        Assert.Equal(HttpStatusCode.OK, polledStart.StatusCode);
        using var startCompleted = await agent.PostAsJsonAsync(
            $"{Route(stationId)}/controller-handshake",
            ControllerHandshakeBody(
                "controller.lifecycle",
                heartbeatSequence: 3,
                commandSequence: startSequence,
                acknowledgedCommandSequence: startSequence,
                completed: true,
                commandId: startCommand.GetProperty("commandId").GetString(),
                commandFencingToken: startCommand
                    .GetProperty("fencingToken")
                    .GetInt64(),
                observedState: "Execute",
                reason: "start controller command complete"));
        Assert.Equal(HttpStatusCode.OK, startCompleted.StatusCode);
        using var executing = await agent.PostAsJsonAsync(
            CommandRoute(stationId, "acknowledge"),
            new
            {
                ownerInstanceId = AgentInstanceId,
                fencingToken = agentFencingToken,
                commandId = startCommand.GetProperty("commandId").GetString(),
                controllerSessionId = "controller.lifecycle",
                commandSequence = startSequence,
                observedMode = "Automatic",
                observedState = "Execute",
                stateSequence = 3,
                reason = "start handshake complete"
            });
        using var executingJson = await ReadJsonAsync(executing);
        Assert.Equal(HttpStatusCode.OK, executing.StatusCode);
        Assert.Equal("Execute", executingJson.RootElement.GetProperty("state").GetString());

        using var safetyLoss = await agent.PostAsJsonAsync(
            $"{Route(stationId)}/readiness",
            new
            {
                readiness = Readiness(ready: true, safetyPermitGranted: false),
                reason = "guard circuit opened"
            });
        using var safetyJson = await ReadJsonAsync(safetyLoss);
        Assert.Equal(HttpStatusCode.OK, safetyLoss.StatusCode);
        Assert.Equal("Aborting", safetyJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(7, safetyJson.RootElement.GetProperty("revision").GetInt64());
        Assert.False(safetyJson.RootElement
            .GetProperty("readiness")
            .GetProperty("safetyPermitGranted")
            .GetBoolean());
        var audit = safetyJson.RootElement.GetProperty("transitionAudit").EnumerateArray().ToArray();
        Assert.Equal(5, audit.Length);
        Assert.Equal("SafetyPermitLost", audit[^1].GetProperty("trigger").GetString());
        Assert.Equal(
            ApiTestAuthentication.StationAgentActorId,
            audit[^1].GetProperty("actorId").GetString());
        Assert.Equal("guard circuit opened", audit[^1].GetProperty("reason").GetString());

        using var get = await operatorClient.GetAsync(Route(stationId));
        using var getJson = await ReadJsonAsync(get);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(
            safetyJson.RootElement.GetProperty("revision").GetInt64(),
            getJson.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(
            safetyJson.RootElement.GetProperty("transitionAudit").GetRawText(),
            getJson.RootElement.GetProperty("transitionAudit").GetRawText());

        using var facts = await operatorClient.GetAsync(
            $"{Route(stationId)}/facts?afterSequence=0&pageSize=100");
        using var factsJson = await ReadJsonAsync(facts);
        Assert.Equal(HttpStatusCode.OK, facts.StatusCode);
        var factItems = factsJson.RootElement.EnumerateArray().ToArray();
        Assert.Equal(8, factItems.Length);
        Assert.Equal(
            factItems[^2].GetProperty("factSha256").GetString(),
            factItems[^1].GetProperty("previousFactSha256").GetString());
    }

    [Fact]
    public async Task LifecycleApiMapsInvalidStateAndMissingPrerequisitesToProblemDetails()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unsafeStation = $"station.not-ready.{suffix}";
        var stoppedStation = $"station.stopped.{suffix}";
        using var engineering = Client(ApiTestAuthentication.EngineeringToken);
        using var operatorClient = Client(ApiTestAuthentication.OperatorToken);
        Assert.Equal(
            HttpStatusCode.Created,
            (await engineering.PutAsJsonAsync(
                Route(unsafeStation),
                CreateBody(ready: false, reason: "register incomplete station"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await engineering.PutAsJsonAsync(
                Route(stoppedStation),
                CreateBody(ready: true, reason: "register ready station"))).StatusCode);

        using var missingPrerequisites = await operatorClient.PostAsJsonAsync(
            CommandRoute(unsafeStation, "reset"),
            new { reason = "attempt reset" });
        using var missingJson = await ReadJsonAsync(missingPrerequisites);
        Assert.Equal(HttpStatusCode.Conflict, missingPrerequisites.StatusCode);
        Assert.Equal(
            "Conflict.Runtime.StationPrerequisitesNotSatisfied",
            missingJson.RootElement.GetProperty("title").GetString());

        using var invalidState = await operatorClient.PostAsJsonAsync(
            CommandRoute(stoppedStation, "start"),
            new { reason = "start before reset" });
        using var invalidJson = await ReadJsonAsync(invalidState);
        Assert.Equal(HttpStatusCode.Conflict, invalidState.StatusCode);
        Assert.Equal(
            "Conflict.Runtime.StationStateTransitionRejected",
            invalidJson.RootElement.GetProperty("title").GetString());

        using var missingReason = await operatorClient.PostAsJsonAsync(
            CommandRoute(stoppedStation, "reset"),
            new { });
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);

        using var unknown = await operatorClient.PostAsJsonAsync(
            CommandRoute(stoppedStation, "restart"),
            new { reason = "unknown command" });
        using var unknownJson = await ReadJsonAsync(unknown);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(
            "Validation.Runtime.StationLifecycleCommand",
            unknownJson.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task LifecycleApiEnforcesEngineeringAgentAndOperatorBoundaries()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station.authorization.{suffix}";
        using var engineering = Client(ApiTestAuthentication.EngineeringToken);
        using var operatorClient = Client(ApiTestAuthentication.OperatorToken);
        using var agent = Client(ApiTestAuthentication.StationAgentToken);
        agent.DefaultRequestHeaders.Add(
            "X-OpenLineOps-Agent-Lease",
            LeaseHandle);

        using var operatorCreate = await operatorClient.PutAsJsonAsync(
            Route(stationId),
            CreateBody(ready: true, reason: "operator cannot create"));
        Assert.Equal(HttpStatusCode.Forbidden, operatorCreate.StatusCode);

        using var created = await engineering.PutAsJsonAsync(
            Route(stationId),
            CreateBody(ready: true, reason: "engineering creates"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var operatorMode = await operatorClient.PostAsJsonAsync(
            $"{Route(stationId)}/mode",
            new { mode = "Maintenance", reason = "operator cannot change mode" });
        Assert.Equal(HttpStatusCode.Forbidden, operatorMode.StatusCode);

        using var engineeringCommand = await engineering.PostAsJsonAsync(
            CommandRoute(stationId, "reset"),
            new { reason = "engineering cannot run station" });
        Assert.Equal(HttpStatusCode.Forbidden, engineeringCommand.StatusCode);

        using var operatorReadiness = await operatorClient.PostAsJsonAsync(
            $"{Route(stationId)}/readiness",
            new
            {
                readiness = Readiness(ready: true, safetyPermitGranted: true),
                reason = "operator cannot report readiness"
            });
        Assert.Equal(HttpStatusCode.Forbidden, operatorReadiness.StatusCode);

        using var operatorAcknowledge = await operatorClient.PostAsJsonAsync(
            CommandRoute(stationId, "acknowledge"),
            new { reason = "operator cannot acknowledge controller transition" });
        Assert.Equal(HttpStatusCode.Forbidden, operatorAcknowledge.StatusCode);

        using var wrongStationAgent = await agent.PostAsJsonAsync(
            $"{Route(stationId)}/readiness",
            new
            {
                readiness = Readiness(ready: true, safetyPermitGranted: true),
                reason = "agent cannot report another station"
            });
        Assert.Equal(HttpStatusCode.Forbidden, wrongStationAgent.StatusCode);
        using var wrongStationCommand = await agent.GetAsync(
            $"{Route(stationId)}/controller-command"
            + $"?ownerInstanceId={AgentInstanceId}&fencingToken=1");
        Assert.Equal(HttpStatusCode.Forbidden, wrongStationCommand.StatusCode);

        using var engineeringGet = await engineering.GetAsync(Route(stationId));
        Assert.Equal(HttpStatusCode.Forbidden, engineeringGet.StatusCode);
        using var operatorGet = await operatorClient.GetAsync(Route(stationId));
        Assert.Equal(HttpStatusCode.OK, operatorGet.StatusCode);
    }

    [Fact]
    public async Task LifecycleApiChangesModeOnlyWithCanonicalTokenAndMandatoryReason()
    {
        var stationId = $"station.mode.{Guid.NewGuid():N}";
        using var engineering = Client(ApiTestAuthentication.EngineeringToken);
        Assert.Equal(
            HttpStatusCode.Created,
            (await engineering.PutAsJsonAsync(
                Route(stationId),
                CreateBody(ready: true, reason: "create mode station"))).StatusCode);

        using var changed = await engineering.PostAsJsonAsync(
            $"{Route(stationId)}/mode",
            new { mode = "Maintenance", reason = "planned maintenance" });
        using var changedJson = await ReadJsonAsync(changed);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("Maintenance", changedJson.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, changedJson.RootElement.GetProperty("revision").GetInt64());

        using var nonCanonical = await engineering.PostAsJsonAsync(
            $"{Route(stationId)}/mode",
            new { mode = "maintenance", reason = "wrong token casing" });
        Assert.Equal(HttpStatusCode.BadRequest, nonCanonical.StatusCode);

        using var blankReason = await engineering.PostAsJsonAsync(
            $"{Route(stationId)}/mode",
            new { mode = "Automatic", reason = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blankReason.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(string token)
    {
        return _factory.CreateAuthenticatedClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false },
            token);
    }

    private static string Route(string stationId) =>
        $"/api/stations/{stationId}/lifecycle";

    private static string CommandRoute(string stationId, string command) =>
        $"{Route(stationId)}/commands/{command}";

    private static object CreateBody(bool ready, string reason) => new
    {
        mode = "Automatic",
        readiness = Readiness(ready, safetyPermitGranted: ready),
        reason
    };

    private static object Readiness(bool ready, bool safetyPermitGranted) => new
    {
        interlocksSatisfied = ready,
        homed = ready,
        criticalDevicesHealthy = ready,
        recipeVerified = ready,
        calibrationValid = ready,
        safetyPermitGranted
    };

    private static object ControllerHandshakeBody(
        string controllerSessionId,
        long heartbeatSequence,
        long commandSequence,
        long acknowledgedCommandSequence,
        bool completed,
        string? commandId,
        long commandFencingToken,
        string observedState,
        string reason) => new
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
        confirmedRecipeId = "recipe.lifecycle",
        confirmedRecipeVersion = "1",
        safetyPermitGranted = true,
        sourceTimestampUtc = DateTimeOffset.UtcNow,
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

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

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
                        "recipe.lifecycle",
                        "1",
                        Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"),
                        Guid.Parse(
                            "22222222-2222-2222-2222-222222222222"),
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        }
    }
}
