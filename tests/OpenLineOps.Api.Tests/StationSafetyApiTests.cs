using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Safety;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class StationSafetyApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Endpoint =
        "/api/operations/stations/station-system.api/emergency-stop";
    private static readonly DateTimeOffset RequestedAtUtc =
        new(2026, 7, 11, 10, 15, 0, TimeSpan.Zero);
    private readonly WebApplicationFactory<Program> _factory;

    public StationSafetyApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RequestUsesResolvedAgentIdentityAndExactReplayIsIdempotent()
    {
        var gateway = new RecordingEmergencyGateway(Acknowledge);
        using var factory = Factory(gateway, new DeploymentResolver());
        using var client = factory.CreateClient();
        var request = Body();

        using var first = await client.PostAsJsonAsync(Endpoint, request);
        using var firstJson = await ReadJsonAsync(first);
        using var replay = await client.PostAsJsonAsync(Endpoint, request);
        using var replayJson = await ReadJsonAsync(replay);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("agent.api", firstJson.RootElement.GetProperty("agentId").GetString());
        Assert.Equal("station.api", firstJson.RootElement.GetProperty("stationId").GetString());
        Assert.Equal("Acknowledged", firstJson.RootElement.GetProperty("status").GetString());
        Assert.True(replayJson.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, gateway.InvocationCount);

        using var events = await client.GetAsync(
            "/api/operations/safety-events?projectId=project.api&applicationId=application.api&stationSystemId=station-system.api");
        using var eventsJson = await ReadJsonAsync(events);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        var persisted = Assert.Single(eventsJson.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(2, persisted.GetProperty("evidence").GetArrayLength());

        using var trace = await client.GetAsync(
            "/api/traceability/station-safety-evidence?projectId=project.api&applicationId=application.api&stationSystemId=station-system.api");
        using var traceJson = await ReadJsonAsync(trace);
        Assert.Equal(HttpStatusCode.OK, trace.StatusCode);
        var traced = Assert.Single(traceJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            request.MessageId,
            traced.GetProperty("messageId").GetGuid().ToString("D"));
        Assert.Equal("Acknowledged", traced.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownBodyFieldCannotSpoofAgentIdentity()
    {
        var gateway = new RecordingEmergencyGateway(Acknowledge);
        using var factory = Factory(gateway, new DeploymentResolver());
        using var client = factory.CreateClient();
        var request = Body();

        using var response = await client.PostAsJsonAsync(Endpoint, new
        {
            request.MessageId,
            request.IdempotencyKey,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.ActorId,
            request.Reason,
            request.RequestedAtUtc,
            agentId = "agent.spoofed"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, gateway.InvocationCount);
    }

    [Fact]
    public async Task UnknownStationFailsClosedBeforeGatewayAndDifferentReplayConflicts()
    {
        var gateway = new RecordingEmergencyGateway(Acknowledge);
        using (var missingFactory = Factory(gateway, new RejectingDeploymentResolver()))
        using (var missingClient = missingFactory.CreateClient())
        using (var missing = await missingClient.PostAsJsonAsync(Endpoint, Body()))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(0, gateway.InvocationCount);
        }

        using var factory = Factory(gateway, new DeploymentResolver());
        using var client = factory.CreateClient();
        var request = Body();
        using var accepted = await client.PostAsJsonAsync(Endpoint, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var conflict = await client.PostAsJsonAsync(Endpoint, new
        {
            request.MessageId,
            request.IdempotencyKey,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.ActorId,
            reason = "Different safety evidence.",
            request.RequestedAtUtc
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, gateway.InvocationCount);
    }

    [Fact]
    public async Task TransportFailureReturnsDurablePendingEvidenceAndStrictInputsAreRejected()
    {
        var gateway = new RecordingEmergencyGateway(_ =>
            throw new IOException("RabbitMQ publish confirmation failed."));
        using var factory = Factory(gateway, new DeploymentResolver());
        using var client = factory.CreateClient();
        var request = Body();

        using var pending = await client.PostAsJsonAsync(Endpoint, request);
        using var pendingJson = await ReadJsonAsync(pending);
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        Assert.Equal("Pending", pendingJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, pendingJson.RootElement.GetProperty("evidence").GetArrayLength());

        using var uppercaseUuid = await client.PostAsJsonAsync(Endpoint, new
        {
            messageId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
            idempotencyKey = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaab",
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.ActorId,
            request.Reason,
            request.RequestedAtUtc
        });
        Assert.Equal(HttpStatusCode.BadRequest, uppercaseUuid.StatusCode);

        using var nonUtc = await client.PostAsJsonAsync(Endpoint, new
        {
            messageId = "33333333-3333-3333-3333-333333333333",
            idempotencyKey = "33333333-3333-3333-3333-333333333334",
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.ActorId,
            request.Reason,
            requestedAtUtc = "2026-07-11T18:15:00.0000000+08:00"
        });
        Assert.Equal(HttpStatusCode.BadRequest, nonUtc.StatusCode);

        using var defaultUtc = await client.PostAsJsonAsync(Endpoint, new
        {
            messageId = "44444444-4444-4444-4444-444444444444",
            idempotencyKey = "44444444-4444-4444-4444-444444444445",
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.ActorId,
            request.Reason,
            requestedAtUtc = "0001-01-01T00:00:00.000Z"
        });
        Assert.Equal(HttpStatusCode.BadRequest, defaultUtc.StatusCode);

        using var futureUtc = await client.PostAsJsonAsync(Endpoint, new
        {
            messageId = "55555555-5555-5555-5555-555555555555",
            idempotencyKey = "55555555-5555-5555-5555-555555555556",
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.ActorId,
            request.Reason,
            requestedAtUtc = "9999-12-31T23:59:59.999Z"
        });
        Assert.Equal(HttpStatusCode.BadRequest, futureUtc.StatusCode);

        using var unknownQuery = await client.GetAsync(
            "/api/operations/safety-events?projectId=project.api&applicationId=application.api&agentId=spoofed");
        Assert.Equal(HttpStatusCode.BadRequest, unknownQuery.StatusCode);
    }

    private WebApplicationFactory<Program> Factory(
        IStationEmergencyStopGateway gateway,
        IStationDeploymentResolver resolver) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStationEmergencyStopRepository>();
                services.RemoveAll<IStationEmergencyStopGateway>();
                services.RemoveAll<IStationDeploymentResolver>();
                services.AddSingleton<IStationEmergencyStopRepository,
                    InMemoryStationEmergencyStopRepository>();
                services.AddSingleton(gateway);
                services.AddSingleton(resolver);
            });
        });

    private static ApiRequestBody Body() => new(
        "11111111-1111-1111-1111-111111111111",
        "11111111-1111-1111-1111-111111111112",
        "project.api",
        "application.api",
        "snapshot.api",
        "operator.api",
        "Operator observed an unsafe guarded-cell entry.",
        RequestedAtUtc.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture));

    private static EmergencyStopAcknowledged Acknowledge(EmergencyStopRequested request) => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        request.MessageId,
        request.IdempotencyKey,
        request.AgentId,
        request.StationId,
        Accepted: true,
        FailureCode: null,
        FailureReason: null,
        RequestedAtUtc.AddSeconds(1));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

    private sealed class DeploymentResolver : IStationDeploymentResolver
    {
        public ValueTask<StationDeploymentRoute> ResolveAsync(
            StationDeploymentRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("station-system.api", request.StationSystemId);
            return ValueTask.FromResult(new StationDeploymentRoute(
                "agent.api",
                "station.api",
                new string('a', 64),
                "line.api"));
        }
    }

    private sealed class RejectingDeploymentResolver : IStationDeploymentResolver
    {
        public ValueTask<StationDeploymentRoute> ResolveAsync(
            StationDeploymentRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unknown deployment.");
    }

    private sealed class RecordingEmergencyGateway(
        Func<EmergencyStopRequested, EmergencyStopAcknowledged> handler) :
        IStationEmergencyStopGateway
    {
        public int InvocationCount { get; private set; }

        public ValueTask<EmergencyStopAcknowledged> RequestEmergencyStopAsync(
            EmergencyStopRequested request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.FromResult(handler(request));
        }
    }

    private sealed record ApiRequestBody(
        string MessageId,
        string IdempotencyKey,
        string ProjectId,
        string ApplicationId,
        string ProjectSnapshotId,
        string ActorId,
        string Reason,
        string RequestedAtUtc);
}
