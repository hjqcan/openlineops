using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeMonitoringProjectionTests
{
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProjectionUpdatesStationTimelineAndAlarmAcknowledgementFromRuntimeEvents()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var projection = new RuntimeMonitoringProjection(repository);
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher([projection]);
        var runner = new RuntimeSessionRunner(
            repository,
            eventPublisher,
            new ScriptedRuntimeCommandExecutor(RuntimeCommandExecutionResult.Failed("scanner returned NG")),
            new DeterministicRuntimeIdProvider(),
            new FixedClock(StartedAtUtc));

        var result = await runner.RunAsync(CreateStartRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Failed, result.Value.Status);

        var stationStatus = Assert.Single(await projection.GetStationStatusesAsync("station-monitoring"));
        Assert.Equal(result.Value.SessionId, stationStatus.LatestSessionId);
        Assert.Equal(RuntimeSessionStatus.Failed, stationStatus.SessionStatus);
        Assert.Equal(1, stationStatus.IncidentCount);
        Assert.True(stationStatus.IsTerminal);

        var timeline = await projection.GetSessionTimelineAsync(result.Value.SessionId);
        Assert.Contains(timeline, entry => entry.EventName == "RuntimeSession.Created");
        Assert.Contains(timeline, entry => entry.EventName == "RuntimeIncident.Recorded");
        Assert.Contains(timeline, entry =>
            entry.EventName == "RuntimeSession.StatusChanged"
            && entry.ToStatus == RuntimeSessionStatus.Failed.ToString());

        var alarm = Assert.Single(await projection.GetAlarmsAsync("station-monitoring"));
        Assert.Equal("Runtime.CommandFailed", alarm.Code);
        Assert.False(alarm.IsAcknowledged);
        Assert.Empty(await projection.GetAlarmsAsync("Station-Monitoring"));

        var acknowledgement = await projection.AcknowledgeAlarmAsync(
            alarm.AlarmId,
            "operator-a",
            StartedAtUtc.AddMinutes(5));

        Assert.True(acknowledgement.IsSuccess);
        Assert.True(acknowledgement.Value.IsAcknowledged);
        Assert.Equal("operator-a", acknowledgement.Value.AcknowledgedBy);
        Assert.Equal(StartedAtUtc.AddMinutes(5), acknowledgement.Value.AcknowledgedAtUtc);
        Assert.Empty(await projection.GetAlarmsAsync("station-monitoring"));

        var acknowledgedAlarm = Assert.Single(await projection.GetAlarmsAsync(
            "station-monitoring",
            includeAcknowledged: true));
        Assert.True(acknowledgedAlarm.IsAcknowledged);
    }

    [Fact]
    public async Task TargetProjectionTracksExactInProgressTerminalLatestAndCaseSensitiveStatuses()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var projection = new RuntimeMonitoringProjection(repository);
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher([projection]);
        var executor = new ObservingRuntimeCommandExecutor(
            projection,
            RuntimeCommandExecutionResult.Completed(),
            RuntimeCommandExecutionResult.Completed(),
            RuntimeCommandExecutionResult.Completed(),
            RuntimeCommandExecutionResult.Failed("lowercase slot failed"),
            RuntimeCommandExecutionResult.Failed("newest system session failed"),
            RuntimeCommandExecutionResult.Completed());
        var runner = new RuntimeSessionRunner(
            repository,
            eventPublisher,
            executor,
            new DeterministicRuntimeIdProvider(),
            new FixedClock(StartedAtUtc));

        var firstRun = await runner.RunAsync(CreateStartRequest(
            "Station.System",
            new ExecutableRuntimeNode(
                new RuntimeNodeId("node-system"),
                "System action",
                new RuntimeCapabilityId("system.run"),
                "Run",
                TimeSpan.FromSeconds(30),
                null,
                new RuntimeActionId("action-system"),
                new RuntimeTargetReference(RuntimeTargetKinds.System, "System.Main")),
            new ExecutableRuntimeNode(
                new RuntimeNodeId("node-group"),
                "Group action",
                new RuntimeCapabilityId("group.run"),
                "Run",
                TimeSpan.FromSeconds(30),
                null,
                new RuntimeActionId("action-group"),
                new RuntimeTargetReference(RuntimeTargetKinds.SlotGroup, "Group.A")),
            new ExecutableRuntimeNode(
                new RuntimeNodeId("node-slot-upper"),
                "Upper slot action",
                new RuntimeCapabilityId("slot.run"),
                "Run",
                TimeSpan.FromSeconds(30),
                null,
                new RuntimeActionId("action-slot-upper"),
                new RuntimeTargetReference(RuntimeTargetKinds.Slot, "Slot.A")),
            new ExecutableRuntimeNode(
                new RuntimeNodeId("node-slot-lower"),
                "Lower slot action",
                new RuntimeCapabilityId("slot.run"),
                "Run",
                TimeSpan.FromSeconds(30),
                null,
                new RuntimeActionId("action-slot-lower"),
                new RuntimeTargetReference(RuntimeTargetKinds.Slot, "slot.a"))));

        Assert.True(firstRun.IsSuccess);
        Assert.Equal(RuntimeSessionStatus.Failed, firstRun.Value.Status);
        Assert.Collection(
            executor.ObservedInProgressStatuses.Take(4),
            status => AssertInProgressTarget(status, RuntimeTargetKinds.System, "System.Main"),
            status => AssertInProgressTarget(status, RuntimeTargetKinds.SlotGroup, "Group.A"),
            status => AssertInProgressTarget(status, RuntimeTargetKinds.Slot, "Slot.A"),
            status => AssertInProgressTarget(status, RuntimeTargetKinds.Slot, "slot.a"));

        var firstStatuses = await projection.GetTargetStatusesAsync("Station.System");
        Assert.Equal(4, firstStatuses.Count);
        AssertTargetStatus(firstStatuses, RuntimeTargetKinds.System, "System.Main", RuntimeCommandStatus.Completed, null);
        AssertTargetStatus(firstStatuses, RuntimeTargetKinds.SlotGroup, "Group.A", RuntimeCommandStatus.Completed, null);
        AssertTargetStatus(firstStatuses, RuntimeTargetKinds.Slot, "Slot.A", RuntimeCommandStatus.Completed, null);
        AssertTargetStatus(
            firstStatuses,
            RuntimeTargetKinds.Slot,
            "slot.a",
            RuntimeCommandStatus.Failed,
            "lowercase slot failed");

        var secondRun = await runner.RunAsync(CreateStartRequest(
            "Station.System",
            new ExecutableRuntimeNode(
                new RuntimeNodeId("node-system-newest"),
                "Newest system action",
                new RuntimeCapabilityId("system.run"),
                "Run",
                TimeSpan.FromSeconds(30),
                null,
                new RuntimeActionId("action-system-newest"),
                new RuntimeTargetReference(RuntimeTargetKinds.System, "System.Main"))));

        Assert.True(secondRun.IsSuccess);
        var latestSystem = Assert.Single(
            await projection.GetTargetStatusesAsync("Station.System"),
            status => status.TargetKind == RuntimeTargetKinds.System);
        Assert.Equal(secondRun.Value.SessionId, latestSystem.SessionId);
        Assert.Equal("action-system-newest", latestSystem.ActionId);
        Assert.Equal(RuntimeCommandStatus.Failed, latestSystem.CommandStatus);
        Assert.Equal("newest system session failed", latestSystem.FailureReason);
        Assert.True(latestSystem.IsTerminal);

        var lowerCaseStationRun = await runner.RunAsync(CreateStartRequest(
            "station.system",
            new ExecutableRuntimeNode(
                new RuntimeNodeId("node-lowercase-station"),
                "Lowercase station action",
                new RuntimeCapabilityId("system.run"),
                "Run",
                TimeSpan.FromSeconds(30),
                null,
                new RuntimeActionId("action-lowercase-station"),
                new RuntimeTargetReference(RuntimeTargetKinds.System, "System.Main"))));

        Assert.True(lowerCaseStationRun.IsSuccess);
        Assert.Single(await projection.GetStationStatusesAsync("Station.System"));
        Assert.Single(await projection.GetStationStatusesAsync("station.system"));
        Assert.Empty(await projection.GetStationStatusesAsync("STATION.SYSTEM"));
        Assert.Single(await projection.GetTargetStatusesAsync("station.system"));
        Assert.Empty(await projection.GetTargetStatusesAsync("STATION.SYSTEM"));
        Assert.Equal(2, (await projection.GetStationStatusesAsync()).Count);
    }

    private static StartRuntimeSessionRequest CreateStartRequest(
        string stationSystemId = "station-monitoring",
        params ExecutableRuntimeNode[]? nodes)
    {
        var executableNodes = nodes is { Length: > 0 }
            ? nodes
            :
            [
                new ExecutableRuntimeNode(
                    new RuntimeNodeId("node-scan"),
                    "Scan barcode",
                    new RuntimeCapabilityId("device.scanner"),
                    "Scan",
                    TimeSpan.FromSeconds(30),
                    null,
                    new RuntimeActionId("node-scan:action:1"),
                    new RuntimeTargetReference(RuntimeTargetKinds.System, "system.scanner"))
            ];

        return new StartRuntimeSessionRequest(
            new StationId(stationSystemId),
            new ConfigurationSnapshotId("snapshot-monitoring"),
            new RecipeSnapshotId("recipe-monitoring"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process-monitoring"),
                new ProcessVersionId("process-monitoring@1.0.0"),
                executableNodes),
            new RuntimeSessionTraceMetadata(
                null,
                null,
                null,
                null,
                "runtime-monitoring-tests",
                "project-monitoring",
                "application-monitoring",
                "snapshot-monitoring",
                "topology-monitoring"));
    }

    private static void AssertInProgressTarget(
        RuntimeTargetStatusProjection status,
        string targetKind,
        string targetId)
    {
        Assert.Equal(targetKind, status.TargetKind);
        Assert.Equal(targetId, status.TargetId);
        Assert.Equal(RuntimeCommandStatus.InProgress, status.CommandStatus);
        Assert.False(status.IsTerminal);
        Assert.Null(status.FailureReason);
    }

    private static void AssertTargetStatus(
        IEnumerable<RuntimeTargetStatusProjection> statuses,
        string targetKind,
        string targetId,
        RuntimeCommandStatus expectedStatus,
        string? expectedFailureReason)
    {
        var status = Assert.Single(
            statuses,
            candidate => candidate.TargetKind == targetKind && candidate.TargetId == targetId);
        Assert.Equal(expectedStatus, status.CommandStatus);
        Assert.Equal(expectedFailureReason, status.FailureReason);
        Assert.True(status.IsTerminal);
        Assert.Equal(StartedAtUtc, status.LastTransitionAtUtc);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class DeterministicRuntimeIdProvider : IRuntimeIdProvider
    {
        private int _value;

        public RuntimeSessionId NewSessionId()
        {
            return new RuntimeSessionId(NextGuid());
        }

        public RuntimeStepId NewStepId()
        {
            return new RuntimeStepId(NextGuid());
        }

        public RuntimeCommandId NewCommandId()
        {
            return new RuntimeCommandId(NextGuid());
        }

        private Guid NextGuid()
        {
            _value++;
            return Guid.Parse($"00000000-0000-0000-0000-{_value:000000000000}");
        }
    }

    private sealed class ScriptedRuntimeCommandExecutor : IRuntimeCommandExecutor
    {
        private readonly Queue<RuntimeCommandExecutionResult> _results;

        public ScriptedRuntimeCommandExecutor(params RuntimeCommandExecutionResult[] results)
        {
            _results = new Queue<RuntimeCommandExecutionResult>(results);
        }

        public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var result = _results.Count == 0
                ? RuntimeCommandExecutionResult.Completed()
                : _results.Dequeue();

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ObservingRuntimeCommandExecutor : IRuntimeCommandExecutor
    {
        private readonly RuntimeMonitoringProjection _projection;
        private readonly Queue<RuntimeCommandExecutionResult> _results;

        public ObservingRuntimeCommandExecutor(
            RuntimeMonitoringProjection projection,
            params RuntimeCommandExecutionResult[] results)
        {
            _projection = projection;
            _results = new Queue<RuntimeCommandExecutionResult>(results);
        }

        public List<RuntimeTargetStatusProjection> ObservedInProgressStatuses { get; } = [];

        public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var current = Assert.Single(
                await _projection.GetTargetStatusesAsync(context.StationId.Value, cancellationToken),
                status => status.TargetKind == context.TargetKind
                    && status.TargetId == context.TargetId);
            ObservedInProgressStatuses.Add(current);

            return _results.Count == 0
                ? RuntimeCommandExecutionResult.Completed()
                : _results.Dequeue();
        }
    }
}
