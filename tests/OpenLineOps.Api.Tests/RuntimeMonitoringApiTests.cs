using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Api.Tests;

public sealed class RuntimeMonitoringApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RuntimeMonitoringApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task RuntimeProgressHubCorsAllowsDesktopViteOrigin()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Options,
            "/hubs/runtime-progress/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "http://127.0.0.1:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "http://127.0.0.1:5173",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var credentials));
        Assert.Equal("true", credentials.Single());
    }

    [Fact]
    public async Task MonitoringApiReturnsStrictCaseSensitiveStationSystemAndTargetStatuses()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationSystemId = $"Station.System.Api.{suffix}";
        var sessionId = await PublishTargetLifecycleAsync(stationSystemId, suffix);

        using var targetResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/targets?stationSystemId={Uri.EscapeDataString(stationSystemId)}");
        using var targetDocument = await ReadJsonAsync(targetResponse);
        var targets = targetDocument.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, targetResponse.StatusCode);
        Assert.Equal(3, targets.Length);
        Assert.All(targets, target => Assert.Equal(
            [
                "actionId",
                "commandStatus",
                "failureReason",
                "isTerminal",
                "lastTransitionAtUtc",
                "sessionId",
                "stationSystemId",
                "targetId",
                "targetKind"
            ],
            target.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)));
        AssertTargetResponse(
            targets,
            sessionId,
            RuntimeTargetKinds.System,
            "System.Main",
            "Completed",
            null);
        AssertTargetResponse(
            targets,
            sessionId,
            RuntimeTargetKinds.SlotGroup,
            "Group.Main",
            "Completed",
            null);
        AssertTargetResponse(
            targets,
            sessionId,
            RuntimeTargetKinds.Slot,
            "Slot.Main",
            "Failed",
            "slot execution failed");

        using var wrongCaseTargetResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/targets?stationSystemId={Uri.EscapeDataString(stationSystemId.ToLowerInvariant())}");
        using var wrongCaseTargetDocument = await ReadJsonAsync(wrongCaseTargetResponse);
        Assert.Empty(wrongCaseTargetDocument.RootElement.GetProperty("items").EnumerateArray());

        using var stationResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/stations?stationSystemId={Uri.EscapeDataString(stationSystemId)}");
        using var stationDocument = await ReadJsonAsync(stationResponse);
        var station = Assert.Single(stationDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(stationSystemId, station.GetProperty("stationSystemId").GetString());
        Assert.False(station.TryGetProperty("stationId", out _));

        using var sessionResponse = await _client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionDocument = await ReadJsonAsync(sessionResponse);
        Assert.Equal(stationSystemId, sessionDocument.RootElement.GetProperty("stationSystemId").GetString());
        Assert.False(sessionDocument.RootElement.TryGetProperty("stationId", out _));

        using var timelineResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/sessions/{sessionId}/timeline");
        using var timelineDocument = await ReadJsonAsync(timelineResponse);
        var timelineEntry = Assert.Single(
            timelineDocument.RootElement.GetProperty("items").EnumerateArray(),
            entry => entry.GetProperty("eventName").GetString() == "RuntimeSession.Created");
        Assert.Equal(stationSystemId, timelineEntry.GetProperty("stationSystemId").GetString());
        Assert.False(timelineEntry.TryGetProperty("stationId", out _));

        using var alarmResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/alarms?stationSystemId={Uri.EscapeDataString(stationSystemId)}");
        using var alarmDocument = await ReadJsonAsync(alarmResponse);
        var alarm = Assert.Single(alarmDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(stationSystemId, alarm.GetProperty("stationSystemId").GetString());
        Assert.False(alarm.TryGetProperty("stationId", out _));

        using var wrongCaseAlarmResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/alarms?stationSystemId={Uri.EscapeDataString(stationSystemId.ToLowerInvariant())}");
        using var wrongCaseAlarmDocument = await ReadJsonAsync(wrongCaseAlarmResponse);
        Assert.Empty(wrongCaseAlarmDocument.RootElement.GetProperty("items").EnumerateArray());

        var recoveryStationSystemId = $"Station.System.Recovery.{suffix}";
        var recoverySessionId = await PublishRunningTargetAsync(
            recoveryStationSystemId,
            $"recovery-{suffix}");
        using var runningTargetResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/targets?stationSystemId={Uri.EscapeDataString(recoveryStationSystemId)}");
        using var runningTargetDocument = await ReadJsonAsync(runningTargetResponse);
        var runningTarget = Assert.Single(
            runningTargetDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("InProgress", runningTarget.GetProperty("commandStatus").GetString());
        Assert.False(runningTarget.GetProperty("isTerminal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, runningTarget.GetProperty("failureReason").ValueKind);

        using var recoveryResponse = await _client.GetAsync("/api/runtime/sessions/recovery-plan");
        using var recoveryDocument = await ReadJsonAsync(recoveryResponse);
        var recoveryCandidate = Assert.Single(
            recoveryDocument.RootElement.GetProperty("candidates").EnumerateArray(),
            candidate => candidate.GetProperty("sessionId").GetGuid() == recoverySessionId);
        Assert.Equal(
            recoveryStationSystemId,
            recoveryCandidate.GetProperty("stationSystemId").GetString());
        Assert.False(recoveryCandidate.TryGetProperty("stationId", out _));
    }

    [Fact]
    public async Task RuntimeProgressHubPublishesExactTargetLifecycleAndStationSystemIdentity()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationSystemId = $"Station.System.SignalR.{suffix}";
        var targetStatuses = new ConcurrentQueue<RuntimeTargetStatusResponse>();
        var stationStatusReceived = new TaskCompletionSource<RuntimeStationStatusResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failedTargetReceived = new TaskCompletionSource<RuntimeTargetStatusResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var connection = CreateHubConnection();
        connection.On<RuntimeStationStatusResponse>("StationStatusChanged", status =>
        {
            if (status.StationSystemId == stationSystemId)
            {
                stationStatusReceived.TrySetResult(status);
            }
        });
        connection.On<RuntimeTargetStatusResponse>("TargetStatusChanged", status =>
        {
            if (status.StationSystemId != stationSystemId)
            {
                return;
            }

            targetStatuses.Enqueue(status);
            if (status.TargetKind == RuntimeTargetKinds.Slot
                && status.CommandStatus == "Failed")
            {
                failedTargetReceived.TrySetResult(status);
            }
        });

        await connection.StartAsync();
        var sessionId = await PublishTargetLifecycleAsync(stationSystemId, suffix);
        var stationStatus = await stationStatusReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failedTarget = await failedTargetReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(stationSystemId, stationStatus.StationSystemId);
        Assert.Equal(sessionId, stationStatus.LatestSessionId);
        Assert.Equal(stationSystemId, failedTarget.StationSystemId);
        Assert.Equal(sessionId, failedTarget.SessionId);
        Assert.Equal("slot execution failed", failedTarget.FailureReason);
        Assert.Contains(targetStatuses, status =>
            status.TargetKind == RuntimeTargetKinds.System
            && status.TargetId == "System.Main"
            && status.CommandStatus == "InProgress"
            && !status.IsTerminal);
        Assert.Contains(targetStatuses, status =>
            status.TargetKind == RuntimeTargetKinds.System
            && status.CommandStatus == "Completed"
            && status.IsTerminal);
        Assert.Contains(targetStatuses, status =>
            status.TargetKind == RuntimeTargetKinds.SlotGroup
            && status.CommandStatus == "InProgress"
            && !status.IsTerminal);
        Assert.Contains(targetStatuses, status =>
            status.TargetKind == RuntimeTargetKinds.SlotGroup
            && status.CommandStatus == "Completed"
            && status.IsTerminal);
        Assert.Contains(targetStatuses, status =>
            status.TargetKind == RuntimeTargetKinds.Slot
            && status.CommandStatus == "InProgress"
            && !status.IsTerminal);
        Assert.Contains(targetStatuses, status =>
            status.TargetKind == RuntimeTargetKinds.Slot
            && status.CommandStatus == "Failed"
            && status.IsTerminal
            && status.FailureReason == "slot execution failed");
    }

    private HubConnection CreateHubConnection()
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/runtime-progress"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();
    }

    private async Task<Guid> PublishTargetLifecycleAsync(string stationSystemId, string suffix)
    {
        var repository = _factory.Services.GetRequiredService<IRuntimeSessionRepository>();
        var publisher = _factory.Services.GetRequiredService<IRuntimeDomainEventPublisher>();
        var session = RuntimeSession.Create(
            RuntimeSessionId.New(),
            new StationId(stationSystemId),
            new ProcessDefinitionId($"process-{suffix}"),
            new ProcessVersionId($"process-{suffix}@1"),
            new ConfigurationSnapshotId($"configuration-{suffix}"),
            new RecipeSnapshotId($"recipe-{suffix}"),
            DateTimeOffset.UtcNow,
            new RuntimeSessionTraceMetadata(
                null,
                null,
                null,
                null,
                "runtime-monitoring-api-tests",
                $"project-{suffix}",
                $"application-{suffix}",
                $"snapshot-{suffix}",
                $"topology-{suffix}"));
        var transitionAtUtc = DateTimeOffset.UtcNow;
        session.Start(transitionAtUtc);

        AddTargetLifecycle(
            session,
            suffix,
            1,
            "action-system",
            RuntimeTargetKinds.System,
            "System.Main",
            shouldFail: false,
            ref transitionAtUtc);
        AddTargetLifecycle(
            session,
            suffix,
            2,
            "action-group",
            RuntimeTargetKinds.SlotGroup,
            "Group.Main",
            shouldFail: false,
            ref transitionAtUtc);
        AddTargetLifecycle(
            session,
            suffix,
            3,
            "action-slot",
            RuntimeTargetKinds.Slot,
            "Slot.Main",
            shouldFail: true,
            ref transitionAtUtc);
        session.Fail(
            transitionAtUtc.AddMilliseconds(1),
            "Runtime.TargetStatusTestFailed",
            "Target status test session failed.");

        var events = session.DomainEvents.ToArray();
        await repository.SaveAsync(session);
        await publisher.PublishAsync(events);
        session.ClearDomainEvents();

        return session.Id.Value;
    }

    private async Task<Guid> PublishRunningTargetAsync(string stationSystemId, string suffix)
    {
        var repository = _factory.Services.GetRequiredService<IRuntimeSessionRepository>();
        var publisher = _factory.Services.GetRequiredService<IRuntimeDomainEventPublisher>();
        var session = RuntimeSession.Create(
            RuntimeSessionId.New(),
            new StationId(stationSystemId),
            new ProcessDefinitionId($"process-{suffix}"),
            new ProcessVersionId($"process-{suffix}@1"),
            new ConfigurationSnapshotId($"configuration-{suffix}"),
            new RecipeSnapshotId($"recipe-{suffix}"),
            DateTimeOffset.UtcNow,
            new RuntimeSessionTraceMetadata(
                null,
                null,
                null,
                null,
                "runtime-monitoring-api-tests",
                $"project-{suffix}",
                $"application-{suffix}",
                $"snapshot-{suffix}",
                $"topology-{suffix}"));
        var transitionAtUtc = DateTimeOffset.UtcNow;
        session.Start(transitionAtUtc);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId($"node-{suffix}"),
            "Running target",
            transitionAtUtc = transitionAtUtc.AddMilliseconds(1),
            new RuntimeActionId("action-running"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "System.Running"));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("target.capability.running"),
            "Execute",
            transitionAtUtc = transitionAtUtc.AddMilliseconds(1),
            TimeSpan.FromSeconds(30));
        session.AcceptCommand(command.Id, transitionAtUtc = transitionAtUtc.AddMilliseconds(1));
        session.StartCommand(command.Id, transitionAtUtc.AddMilliseconds(1));

        var events = session.DomainEvents.ToArray();
        await repository.SaveAsync(session);
        await publisher.PublishAsync(events);
        session.ClearDomainEvents();

        return session.Id.Value;
    }

    private static void AddTargetLifecycle(
        RuntimeSession session,
        string suffix,
        int ordinal,
        string actionId,
        string targetKind,
        string targetId,
        bool shouldFail,
        ref DateTimeOffset transitionAtUtc)
    {
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId($"node-{suffix}-{ordinal}"),
            $"Target {ordinal}",
            transitionAtUtc = transitionAtUtc.AddMilliseconds(1),
            new RuntimeActionId(actionId),
            new RuntimeTargetReference(targetKind, targetId));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId($"target.capability.{ordinal}"),
            "Execute",
            transitionAtUtc = transitionAtUtc.AddMilliseconds(1),
            TimeSpan.FromSeconds(30));
        session.AcceptCommand(command.Id, transitionAtUtc = transitionAtUtc.AddMilliseconds(1));
        session.StartCommand(command.Id, transitionAtUtc = transitionAtUtc.AddMilliseconds(1));

        if (shouldFail)
        {
            session.FailCommand(
                command.Id,
                "slot execution failed",
                transitionAtUtc = transitionAtUtc.AddMilliseconds(1));
            session.FailStep(
                step.Id,
                "slot execution failed",
                transitionAtUtc = transitionAtUtc.AddMilliseconds(1));
            return;
        }

        session.CompleteCommand(
            command.Id,
            null,
            transitionAtUtc = transitionAtUtc.AddMilliseconds(1));
        session.CompleteStep(step.Id, transitionAtUtc = transitionAtUtc.AddMilliseconds(1));
    }

    private static void AssertTargetResponse(
        IEnumerable<JsonElement> targets,
        Guid sessionId,
        string targetKind,
        string targetId,
        string commandStatus,
        string? failureReason)
    {
        var target = Assert.Single(
            targets,
            item => item.GetProperty("targetKind").GetString() == targetKind
                && item.GetProperty("targetId").GetString() == targetId);
        Assert.Equal(sessionId, target.GetProperty("sessionId").GetGuid());
        Assert.Equal(commandStatus, target.GetProperty("commandStatus").GetString());
        Assert.True(target.GetProperty("isTerminal").GetBoolean());
        Assert.Equal(failureReason, target.GetProperty("failureReason").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
