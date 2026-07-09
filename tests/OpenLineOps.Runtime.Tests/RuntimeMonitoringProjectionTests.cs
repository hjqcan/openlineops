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

    private static StartRuntimeSessionRequest CreateStartRequest()
    {
        return new StartRuntimeSessionRequest(
            new StationId("station-monitoring"),
            new ConfigurationSnapshotId("snapshot-monitoring"),
            new RecipeSnapshotId("recipe-monitoring"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process-monitoring"),
                new ProcessVersionId("process-monitoring@1.0.0"),
                [
                    new ExecutableRuntimeNode(
                        new RuntimeNodeId("node-scan"),
                        "Scan barcode",
                        new RuntimeCapabilityId("device.scanner"),
                        "Scan",
                        TimeSpan.FromSeconds(30))
                ]));
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
}
