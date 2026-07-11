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
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
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
            $"/api/runtime/monitoring/targets?{MonitoringQuery(suffix, stationSystemId)}");
        using var targetDocument = await ReadJsonAsync(targetResponse);
        var targets = targetDocument.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, targetResponse.StatusCode);
        Assert.Equal(3, targets.Length);
        Assert.All(targets, target => Assert.Equal(
            [
                "actionId",
                "applicationId",
                "commandStatus",
                "failureReason",
                "isTerminal",
                "lastTransitionAtUtc",
                "operationAttempt",
                "operationId",
                "productionLineDefinitionId",
                "productionRunId",
                "productionUnitIdentity",
                "projectId",
                "projectSnapshotId",
                "runtimeStationId",
                "sessionId",
                "stationSystemId",
                "targetId",
                "targetKind",
                "topologyId"
            ],
            target.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)));
        AssertTargetResponse(
            targets,
            sessionId,
            suffix,
            RuntimeTargetKinds.System,
            "System.Main",
            "Completed",
            null);
        AssertTargetResponse(
            targets,
            sessionId,
            suffix,
            RuntimeTargetKinds.SlotGroup,
            "Group.Main",
            "Completed",
            null);
        AssertTargetResponse(
            targets,
            sessionId,
            suffix,
            RuntimeTargetKinds.Slot,
            "Slot.Main",
            "Failed",
            "slot execution failed");

        using var wrongCaseTargetResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/targets?{MonitoringQuery(suffix, stationSystemId.ToLowerInvariant())}");
        using var wrongCaseTargetDocument = await ReadJsonAsync(wrongCaseTargetResponse);
        Assert.Empty(wrongCaseTargetDocument.RootElement.GetProperty("items").EnumerateArray());

        using var stationResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/stations?{MonitoringQuery(suffix, stationSystemId)}");
        using var stationDocument = await ReadJsonAsync(stationResponse);
        var station = Assert.Single(stationDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(stationSystemId, station.GetProperty("stationSystemId").GetString());
        Assert.Equal($"project-{suffix}", station.GetProperty("projectId").GetString());
        Assert.Equal($"application-{suffix}", station.GetProperty("applicationId").GetString());
        Assert.Equal($"snapshot-{suffix}", station.GetProperty("projectSnapshotId").GetString());
        Assert.Equal($"topology-{suffix}", station.GetProperty("topologyId").GetString());
        Assert.Equal(ProductionRunGuid(suffix), station.GetProperty("productionRunId").GetGuid());
        Assert.Equal($"line-{suffix}", station.GetProperty("productionLineDefinitionId").GetString());
        Assert.Equal($"operation-{suffix}", station.GetProperty("operationId").GetString());
        Assert.Equal(1, station.GetProperty("operationAttempt").GetInt32());
        Assert.Equal(stationSystemId, station.GetProperty("runtimeStationId").GetString());
        AssertProductionUnitIdentity(station.GetProperty("productionUnitIdentity"), suffix);
        Assert.False(station.TryGetProperty("stationId", out _));
        Assert.False(station.TryGetProperty("serialNumber", out _));

        using var wrongApplicationResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/stations?{MonitoringQuery(suffix, stationSystemId, $"application-other-{suffix}")}");
        using var wrongApplicationDocument = await ReadJsonAsync(wrongApplicationResponse);
        Assert.Empty(wrongApplicationDocument.RootElement.GetProperty("items").EnumerateArray());

        using var missingScopeResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/stations?stationSystemId={Uri.EscapeDataString(stationSystemId)}");
        Assert.Equal(HttpStatusCode.BadRequest, missingScopeResponse.StatusCode);

        using var sessionResponse = await _client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionDocument = await ReadJsonAsync(sessionResponse);
        Assert.Equal(stationSystemId, sessionDocument.RootElement.GetProperty("stationSystemId").GetString());
        Assert.False(sessionDocument.RootElement.TryGetProperty("stationId", out _));

        using var timelineResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/sessions/{sessionId}/timeline?{MonitoringQuery(suffix, stationSystemId)}");
        using var timelineDocument = await ReadJsonAsync(timelineResponse);
        var timelineEntry = Assert.Single(
            timelineDocument.RootElement.GetProperty("items").EnumerateArray(),
            entry => entry.GetProperty("eventName").GetString() == "RuntimeSession.Created");
        Assert.Equal(stationSystemId, timelineEntry.GetProperty("stationSystemId").GetString());
        Assert.Equal(ProductionRunGuid(suffix), timelineEntry.GetProperty("productionRunId").GetGuid());
        Assert.Equal($"line-{suffix}", timelineEntry.GetProperty("productionLineDefinitionId").GetString());
        Assert.Equal($"operation-{suffix}", timelineEntry.GetProperty("operationId").GetString());
        Assert.Equal(1, timelineEntry.GetProperty("operationAttempt").GetInt32());
        Assert.Equal(stationSystemId, timelineEntry.GetProperty("runtimeStationId").GetString());
        AssertProductionUnitIdentity(timelineEntry.GetProperty("productionUnitIdentity"), suffix);
        Assert.False(timelineEntry.TryGetProperty("stationId", out _));
        Assert.False(timelineEntry.TryGetProperty("serialNumber", out _));

        using var wrongRunResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/stations?{MonitoringQuery(suffix, stationSystemId, productionRunId: Guid.NewGuid())}");
        using var wrongRunDocument = await ReadJsonAsync(wrongRunResponse);
        Assert.Empty(wrongRunDocument.RootElement.GetProperty("items").EnumerateArray());

        using var unscopedTimelineResponse = await _client.GetAsync(
            $"/api/runtime/monitoring/sessions/{sessionId}/timeline");
        Assert.Equal(HttpStatusCode.BadRequest, unscopedTimelineResponse.StatusCode);

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
            $"/api/runtime/monitoring/targets?{MonitoringQuery($"recovery-{suffix}", recoveryStationSystemId)}");
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
        await connection.InvokeAsync(
            "JoinProductionRunGroup",
            $"project-{suffix}",
            $"application-{suffix}",
            $"snapshot-{suffix}",
            $"topology-{suffix}",
            ProductionRunGuid(suffix));
        var sessionId = await PublishTargetLifecycleAsync(stationSystemId, suffix);
        var stationStatus = await stationStatusReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failedTarget = await failedTargetReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(stationSystemId, stationStatus.StationSystemId);
        Assert.Equal(sessionId, stationStatus.LatestSessionId);
        Assert.Equal($"project-{suffix}", stationStatus.ProjectId);
        Assert.Equal($"application-{suffix}", stationStatus.ApplicationId);
        Assert.Equal($"snapshot-{suffix}", stationStatus.ProjectSnapshotId);
        Assert.Equal($"topology-{suffix}", stationStatus.TopologyId);
        Assert.Equal(ProductionRunGuid(suffix), stationStatus.ProductionRunId);
        Assert.Equal($"line-{suffix}", stationStatus.ProductionLineDefinitionId);
        Assert.Equal($"operation-{suffix}", stationStatus.OperationId);
        Assert.Equal(1, stationStatus.OperationAttempt);
        Assert.Equal(stationSystemId, stationStatus.RuntimeStationId);
        Assert.Equal($"product-model-{suffix}", stationStatus.ProductionUnitIdentity.ModelId);
        Assert.Equal("serialNumber", stationStatus.ProductionUnitIdentity.InputKey);
        Assert.Equal($"UNIT-{suffix}", stationStatus.ProductionUnitIdentity.Value);
        Assert.Equal(stationSystemId, failedTarget.StationSystemId);
        Assert.Equal(sessionId, failedTarget.SessionId);
        Assert.Equal(stationStatus.ProjectId, failedTarget.ProjectId);
        Assert.Equal(stationStatus.ApplicationId, failedTarget.ApplicationId);
        Assert.Equal(stationStatus.ProjectSnapshotId, failedTarget.ProjectSnapshotId);
        Assert.Equal(stationStatus.TopologyId, failedTarget.TopologyId);
        Assert.Equal(stationStatus.ProductionRunId, failedTarget.ProductionRunId);
        Assert.Equal(stationStatus.ProductionLineDefinitionId, failedTarget.ProductionLineDefinitionId);
        Assert.Equal(stationStatus.OperationId, failedTarget.OperationId);
        Assert.Equal(stationStatus.OperationAttempt, failedTarget.OperationAttempt);
        Assert.Equal(stationStatus.RuntimeStationId, failedTarget.RuntimeStationId);
        Assert.Equal(stationStatus.ProductionUnitIdentity, failedTarget.ProductionUnitIdentity);
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

    private static string MonitoringQuery(
        string suffix,
        string stationSystemId,
        string? applicationId = null,
        Guid? productionRunId = null)
    {
        return string.Join(
            "&",
            $"projectId={Uri.EscapeDataString($"project-{suffix}")}",
            $"applicationId={Uri.EscapeDataString(applicationId ?? $"application-{suffix}")}",
            $"projectSnapshotId={Uri.EscapeDataString($"snapshot-{suffix}")}",
            $"topologyId={Uri.EscapeDataString($"topology-{suffix}")}",
            $"productionRunId={Uri.EscapeDataString((productionRunId ?? ProductionRunGuid(suffix)).ToString("D"))}",
            $"stationSystemId={Uri.EscapeDataString(stationSystemId)}");
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
            CreateTraceMetadata(stationSystemId, suffix));
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
            CreateTraceMetadata(stationSystemId, suffix));
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
        string suffix,
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
        Assert.Equal(ProductionRunGuid(suffix), target.GetProperty("productionRunId").GetGuid());
        Assert.Equal($"line-{suffix}", target.GetProperty("productionLineDefinitionId").GetString());
        Assert.Equal($"operation-{suffix}", target.GetProperty("operationId").GetString());
        Assert.Equal(1, target.GetProperty("operationAttempt").GetInt32());
        Assert.Equal(target.GetProperty("stationSystemId").GetString(), target.GetProperty("runtimeStationId").GetString());
        AssertProductionUnitIdentity(target.GetProperty("productionUnitIdentity"), suffix);
        Assert.True(target.GetProperty("isTerminal").GetBoolean());
        Assert.Equal(failureReason, target.GetProperty("failureReason").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static RuntimeSessionTraceMetadata CreateTraceMetadata(
        string stationSystemId,
        string suffix)
    {
        return new RuntimeSessionTraceMetadata(
            new ProductionRunId(ProductionRunGuid(suffix)),
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New(),
            $"line-{suffix}",
            $"operation-{suffix}",
            $"operation-{suffix}@0001",
            1,
            stationSystemId,
            new ProductionUnitIdentity($"product-model-{suffix}", "serialNumber", $"UNIT-{suffix}"),
            null,
            null,
            null,
            null,
            "runtime-monitoring-api-tests",
            $"project-{suffix}",
            $"application-{suffix}",
            $"snapshot-{suffix}",
            $"topology-{suffix}",
            [new ResourceLeaseFenceEvidence(
                new ResourceRequirement(ResourceKind.Station, stationSystemId),
                1,
                DateTimeOffset.MaxValue)]);
    }

    private static Guid ProductionRunGuid(string suffix)
    {
        const string recoveryPrefix = "recovery-";
        var guidText = suffix.StartsWith(recoveryPrefix, StringComparison.Ordinal)
            ? suffix[recoveryPrefix.Length..]
            : suffix;
        return Guid.ParseExact(guidText, "N");
    }

    private static void AssertProductionUnitIdentity(JsonElement productionUnitIdentity, string suffix)
    {
        Assert.Equal($"product-model-{suffix}", productionUnitIdentity.GetProperty("modelId").GetString());
        Assert.Equal("serialNumber", productionUnitIdentity.GetProperty("inputKey").GetString());
        Assert.Equal($"UNIT-{suffix}", productionUnitIdentity.GetProperty("value").GetString());
    }
}
