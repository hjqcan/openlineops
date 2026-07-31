using Microsoft.Data.Sqlite;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeMonitoringProjectionTests
{
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly ProductionRunId MonitoringProductionRunId = new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly RuntimeMonitoringScope MonitoringScope = new(
        "project-monitoring",
        "application-monitoring",
        "snapshot-monitoring",
        "topology-monitoring",
        MonitoringProductionRunId);

    [Fact]
    public async Task ProjectionUpdatesStationTimelineAndAlarmAcknowledgementFromRuntimeEvents()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var projection = new RuntimeMonitoringProjection(repository);
        await projection.InitializeAsync();
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

        var stationStatus = Assert.Single(await projection.GetStationStatusesAsync(
            MonitoringScope,
            "station-monitoring"));
        Assert.Equal(result.Value.SessionId, stationStatus.LatestSessionId);
        Assert.Equal(RuntimeSessionStatus.Failed, stationStatus.SessionStatus);
        Assert.Equal(MonitoringProductionRunId, stationStatus.ProductionRunId);
        Assert.Equal("line.main", stationStatus.ProductionLineDefinitionId);
        Assert.Equal("operation.main", stationStatus.OperationId);
        Assert.Equal(1, stationStatus.OperationAttempt);
        Assert.Equal("station-monitoring", stationStatus.StationSystemId);
        Assert.Equal("product.default", stationStatus.ProductionUnitIdentity.ModelId);
        Assert.Equal("serialNumber", stationStatus.ProductionUnitIdentity.InputKey);
        Assert.Equal("UNIT-DEFAULT", stationStatus.ProductionUnitIdentity.Value);
        Assert.Equal(1, stationStatus.IncidentCount);
        Assert.True(stationStatus.IsTerminal);

        var timeline = await projection.GetSessionTimelineAsync(
            result.Value.SessionId,
            MonitoringScope);
        Assert.Contains(timeline, entry => entry.EventName == "RuntimeSession.Created");
        Assert.Contains(timeline, entry => entry.EventName == "RuntimeIncident.Recorded");
        Assert.Contains(timeline, entry =>
            entry.EventName == "RuntimeSession.StatusChanged"
            && entry.ToStatus == RuntimeSessionStatus.Failed.ToString());
        Assert.All(timeline, entry =>
        {
            Assert.Equal(MonitoringProductionRunId, entry.ProductionRunId);
            Assert.Equal("line.main", entry.ProductionLineDefinitionId);
            Assert.Equal("operation.main", entry.OperationId);
            Assert.Equal(1, entry.OperationAttempt);
            Assert.Equal("station-monitoring", entry.StationSystemId);
            Assert.Equal("UNIT-DEFAULT", entry.ProductionUnitIdentity.Value);
        });
        Assert.Empty(await projection.GetSessionTimelineAsync(
            result.Value.SessionId,
            new RuntimeMonitoringScope(
                MonitoringScope.ProjectId,
                MonitoringScope.ApplicationId,
                MonitoringScope.ProjectSnapshotId,
                MonitoringScope.TopologyId,
                ProductionRunId.New())));

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
    public async Task TargetProjectionTracksExactRunningTerminalLatestAndCaseSensitiveStatuses()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var projection = new RuntimeMonitoringProjection(repository);
        await projection.InitializeAsync();
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
            executor.ObservedRunningStatuses.Take(4),
            status => AssertRunningTarget(status, RuntimeTargetKinds.System, "System.Main"),
            status => AssertRunningTarget(status, RuntimeTargetKinds.SlotGroup, "Group.A"),
            status => AssertRunningTarget(status, RuntimeTargetKinds.Slot, "Slot.A"),
            status => AssertRunningTarget(status, RuntimeTargetKinds.Slot, "slot.a"));

        var firstStatuses = await projection.GetTargetStatusesAsync(MonitoringScope, "Station.System");
        Assert.Equal(4, firstStatuses.Count);
        AssertTargetStatus(firstStatuses, RuntimeTargetKinds.System, "System.Main", ExecutionStatus.Completed, null);
        AssertTargetStatus(firstStatuses, RuntimeTargetKinds.SlotGroup, "Group.A", ExecutionStatus.Completed, null);
        AssertTargetStatus(firstStatuses, RuntimeTargetKinds.Slot, "Slot.A", ExecutionStatus.Completed, null);
        AssertTargetStatus(
            firstStatuses,
            RuntimeTargetKinds.Slot,
            "slot.a",
            ExecutionStatus.Failed,
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
        var secondStatuses = await projection.GetTargetStatusesAsync(MonitoringScope, "Station.System");
        Assert.Single(secondStatuses);
        var latestSystem = Assert.Single(
            secondStatuses,
            status => status.TargetKind == RuntimeTargetKinds.System);
        Assert.Equal(secondRun.Value.SessionId, latestSystem.SessionId);
        Assert.Equal("action-system-newest", latestSystem.ActionId);
        Assert.Equal(ExecutionStatus.Failed, latestSystem.CommandStatus);
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
        Assert.Single(await projection.GetStationStatusesAsync(MonitoringScope, "Station.System"));
        Assert.Single(await projection.GetStationStatusesAsync(MonitoringScope, "station.system"));
        Assert.Empty(await projection.GetStationStatusesAsync(MonitoringScope, "STATION.SYSTEM"));
        Assert.Single(await projection.GetTargetStatusesAsync(MonitoringScope, "station.system"));
        Assert.Empty(await projection.GetTargetStatusesAsync(MonitoringScope, "STATION.SYSTEM"));
        Assert.Equal(2, (await projection.GetStationStatusesAsync(MonitoringScope)).Count);
    }

    [Fact]
    public async Task IdenticalLocalStationAndTargetIdsRemainIsolatedByApplicationSnapshotScope()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var projection = new RuntimeMonitoringProjection(repository);
        await projection.InitializeAsync();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher([projection]);
        var runner = new RuntimeSessionRunner(
            repository,
            eventPublisher,
            new ScriptedRuntimeCommandExecutor(
                RuntimeCommandExecutionResult.Completed(),
                RuntimeCommandExecutionResult.Failed("application-b failed")),
            new DeterministicRuntimeIdProvider(),
            new FixedClock(StartedAtUtc));
        var scopeA = new RuntimeMonitoringScope(
            "project-shared",
            "application-a",
            "snapshot-a",
            "topology-shared");
        var scopeB = new RuntimeMonitoringScope(
            "project-shared",
            "application-b",
            "snapshot-b",
            "topology-shared");

        var runA = await runner.RunAsync(CreateScopedStartRequest(
            scopeA,
            "station.shared",
            TargetNode("node-a", "action-a", RuntimeTargetKinds.Slot, "slot.shared")));
        var runB = await runner.RunAsync(CreateScopedStartRequest(
            scopeB,
            "station.shared",
            TargetNode("node-b", "action-b", RuntimeTargetKinds.Slot, "slot.shared")));

        Assert.True(runA.IsSuccess);
        Assert.True(runB.IsSuccess);
        var stationA = Assert.Single(await projection.GetStationStatusesAsync(scopeA, "station.shared"));
        var stationB = Assert.Single(await projection.GetStationStatusesAsync(scopeB, "station.shared"));
        Assert.Equal(runA.Value.SessionId, stationA.LatestSessionId);
        Assert.Equal(runB.Value.SessionId, stationB.LatestSessionId);
        Assert.Equal("application-a", stationA.ApplicationId);
        Assert.Equal("application-b", stationB.ApplicationId);
        var targetA = Assert.Single(await projection.GetTargetStatusesAsync(scopeA, "station.shared"));
        var targetB = Assert.Single(await projection.GetTargetStatusesAsync(scopeB, "station.shared"));
        Assert.Equal(ExecutionStatus.Completed, targetA.CommandStatus);
        Assert.Equal(ExecutionStatus.Failed, targetB.CommandStatus);
        Assert.Equal("application-b failed", targetB.FailureReason);
        Assert.Empty(await projection.GetStationStatusesAsync(new RuntimeMonitoringScope(
            "project-shared",
            "application-a",
            "snapshot-b",
            "topology-shared")));
    }

    [Fact]
    public async Task IdenticalReleaseAndStationIdentitiesRemainIsolatedByProductionRun()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var projection = new RuntimeMonitoringProjection(repository);
        await projection.InitializeAsync();
        var eventPublisher = new InMemoryRuntimeDomainEventPublisher([projection]);
        var runner = new RuntimeSessionRunner(
            repository,
            eventPublisher,
            new ScriptedRuntimeCommandExecutor(
                RuntimeCommandExecutionResult.Completed(),
                RuntimeCommandExecutionResult.Failed("second production run failed")),
            new DeterministicRuntimeIdProvider(),
            new FixedClock(StartedAtUtc));
        var firstProductionRunId = new ProductionRunId(
            Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var secondProductionRunId = new ProductionRunId(
            Guid.Parse("20000000-0000-0000-0000-000000000002"));
        var unfilteredScope = new RuntimeMonitoringScope(
            "project-run-scope",
            "application-run-scope",
            "snapshot-run-scope",
            "topology-run-scope");
        var firstScope = new RuntimeMonitoringScope(
            unfilteredScope.ProjectId,
            unfilteredScope.ApplicationId,
            unfilteredScope.ProjectSnapshotId,
            unfilteredScope.TopologyId,
            firstProductionRunId);
        var secondScope = new RuntimeMonitoringScope(
            unfilteredScope.ProjectId,
            unfilteredScope.ApplicationId,
            unfilteredScope.ProjectSnapshotId,
            unfilteredScope.TopologyId,
            secondProductionRunId);

        var firstRun = await runner.RunAsync(CreateProductionRunScopedStartRequest(
            firstScope,
            "station.shared",
            TargetNode("node.shared", "action.shared", RuntimeTargetKinds.Slot, "slot.shared")));
        var secondRun = await runner.RunAsync(CreateProductionRunScopedStartRequest(
            secondScope,
            "station.shared",
            TargetNode("node.shared", "action.shared", RuntimeTargetKinds.Slot, "slot.shared")));

        Assert.True(firstRun.IsSuccess);
        Assert.True(secondRun.IsSuccess);
        var firstStation = Assert.Single(await projection.GetStationStatusesAsync(firstScope));
        var secondStation = Assert.Single(await projection.GetStationStatusesAsync(secondScope));
        Assert.Equal(firstRun.Value.SessionId, firstStation.LatestSessionId);
        Assert.Equal(secondRun.Value.SessionId, secondStation.LatestSessionId);
        Assert.Equal(firstProductionRunId, firstStation.ProductionRunId);
        Assert.Equal(secondProductionRunId, secondStation.ProductionRunId);
        Assert.Equal(2, (await projection.GetStationStatusesAsync(unfilteredScope)).Count);

        var firstTarget = Assert.Single(await projection.GetTargetStatusesAsync(firstScope));
        var secondTarget = Assert.Single(await projection.GetTargetStatusesAsync(secondScope));
        Assert.Equal(ExecutionStatus.Completed, firstTarget.CommandStatus);
        Assert.Equal(ExecutionStatus.Failed, secondTarget.CommandStatus);
        Assert.Equal(2, (await projection.GetTargetStatusesAsync(unfilteredScope)).Count);
        Assert.Empty(await projection.GetSessionTimelineAsync(firstRun.Value.SessionId, secondScope));
        Assert.NotEmpty(await projection.GetSessionTimelineAsync(firstRun.Value.SessionId, firstScope));
    }

    [Fact]
    public async Task FileBackedSqliteColdRestartReplaysUnpublishedEventsAndRestoresExactProjection()
    {
        using var database = TemporarySqliteDatabase.Create();
        RuntimeSessionRunResult firstRun;

        using (var repository = new SqliteRuntimeSessionRepository(database.ConnectionString))
        using (var projection = new RuntimeMonitoringProjection(repository))
        {
            await projection.InitializeAsync();
            var runner = new RuntimeSessionRunner(
                repository,
                new InMemoryRuntimeDomainEventPublisher([]),
                new ScriptedRuntimeCommandExecutor(RuntimeCommandExecutionResult.Failed("scanner returned NG")),
                new DeterministicRuntimeIdProvider(),
                new FixedClock(StartedAtUtc));

            var result = await runner.RunAsync(CreateStartRequest());
            Assert.True(result.IsSuccess);
            firstRun = result.Value;
            Assert.Empty(await projection.GetStationStatusesAsync(MonitoringScope));
            Assert.Empty(await projection.GetSessionTimelineAsync(firstRun.SessionId, MonitoringScope));
            Assert.Empty(await projection.GetAlarmsAsync(includeAcknowledged: true));
            Assert.True(await CountRowsAsync(database.ConnectionString, "runtime_monitoring_events") > 0);
        }

        RuntimeStationStatusProjection expectedStation;
        RuntimeTargetStatusProjection expectedTarget;
        RuntimeTimelineEntry[] expectedTimeline;
        RuntimeAlarmProjection expectedAlarm;
        RuntimeSessionRunResult liveRun;
        RuntimeStationStatusProjection expectedLiveStation;
        RuntimeTargetStatusProjection expectedLiveTarget;
        RuntimeTimelineEntry[] expectedLiveTimeline;
        using (var repository = new SqliteRuntimeSessionRepository(database.ConnectionString))
        using (var projection = new RuntimeMonitoringProjection(repository))
        {
            await projection.InitializeAsync();
            Assert.Equal(1, await CountRowsAsync(
                database.ConnectionString,
                "runtime_monitoring_station_statuses"));
            var rawStation = Assert.Single(await repository.ListStationStatusesAsync());
            Assert.Equal(MonitoringScope.ProjectId, rawStation.ProjectId);
            Assert.Equal(MonitoringScope.ApplicationId, rawStation.ApplicationId);
            Assert.Equal(MonitoringScope.ProjectSnapshotId, rawStation.ProjectSnapshotId);
            Assert.Equal(MonitoringScope.TopologyId, rawStation.TopologyId);
            Assert.Equal(MonitoringScope.ProductionRunId, rawStation.ProductionRunId);
            expectedStation = Assert.Single(await projection.GetStationStatusesAsync(MonitoringScope));
            expectedTarget = Assert.Single(await projection.GetTargetStatusesAsync(MonitoringScope));
            expectedTimeline = (await projection.GetSessionTimelineAsync(firstRun.SessionId, MonitoringScope)).ToArray();
            var alarm = Assert.Single(await projection.GetAlarmsAsync());
            var acknowledgement = await projection.AcknowledgeAlarmAsync(
                alarm.AlarmId,
                "operator-cold-restart",
                StartedAtUtc.AddMinutes(10));
            Assert.True(acknowledgement.IsSuccess);
            expectedAlarm = acknowledgement.Value;

            Assert.Equal(RuntimeSessionStatus.Failed, expectedStation.SessionStatus);
            Assert.Equal(ExecutionStatus.Failed, expectedTarget.CommandStatus);
            Assert.True(expectedTimeline.Length > 1);
            Assert.Equal(
                expectedTimeline.Select(entry => entry.EventId).Distinct().Count(),
                expectedTimeline.Length);
            Assert.True(expectedTimeline.Zip(expectedTimeline.Skip(1),
                (left, right) => left.Sequence < right.Sequence).All(value => value));

            var livePublisher = new InMemoryRuntimeDomainEventPublisher([projection]);
            var liveRunner = new RuntimeSessionRunner(
                repository,
                livePublisher,
                new ScriptedRuntimeCommandExecutor(RuntimeCommandExecutionResult.Completed()),
                new DeterministicRuntimeIdProvider(),
                new FixedClock(StartedAtUtc.AddHours(1)));
            var liveResult = await liveRunner.RunAsync(CreateStartRequest("station-monitoring-live"));
            Assert.True(liveResult.IsSuccess);
            liveRun = liveResult.Value;
            expectedLiveStation = Assert.Single(await projection.GetStationStatusesAsync(
                MonitoringScope,
                "station-monitoring-live"));
            expectedLiveTarget = Assert.Single(await projection.GetTargetStatusesAsync(
                MonitoringScope,
                "station-monitoring-live"));
            expectedLiveTimeline = (await projection.GetSessionTimelineAsync(
                liveRun.SessionId,
                MonitoringScope)).ToArray();
            Assert.Equal(RuntimeSessionStatus.Completed, expectedLiveStation.SessionStatus);
            Assert.Equal(ExecutionStatus.Completed, expectedLiveTarget.CommandStatus);
        }

        using (var repository = new SqliteRuntimeSessionRepository(database.ConnectionString))
        using (var projection = new RuntimeMonitoringProjection(repository))
        {
            await projection.InitializeAsync();
            Assert.Equal(expectedStation, Assert.Single(
                await projection.GetStationStatusesAsync(MonitoringScope, "station-monitoring")));
            Assert.Equal(expectedTarget, Assert.Single(
                await projection.GetTargetStatusesAsync(MonitoringScope, "station-monitoring")));
            Assert.Equal(
                expectedTimeline,
                (await projection.GetSessionTimelineAsync(firstRun.SessionId, MonitoringScope)).ToArray());
            Assert.Equal(expectedAlarm, Assert.Single(
                await projection.GetAlarmsAsync(includeAcknowledged: true)));
            Assert.Equal(expectedLiveStation, Assert.Single(await projection.GetStationStatusesAsync(
                MonitoringScope,
                "station-monitoring-live")));
            Assert.Equal(expectedLiveTarget, Assert.Single(await projection.GetTargetStatusesAsync(
                MonitoringScope,
                "station-monitoring-live")));
            Assert.Equal(
                expectedLiveTimeline,
                (await projection.GetSessionTimelineAsync(liveRun.SessionId, MonitoringScope)).ToArray());

            using var repeatedRebuild = new RuntimeMonitoringProjection(repository);
            await repeatedRebuild.InitializeAsync();
            Assert.Equal(2, (await repeatedRebuild.GetStationStatusesAsync(MonitoringScope)).Count);
            Assert.Equal(2, (await repeatedRebuild.GetTargetStatusesAsync(MonitoringScope)).Count);
            Assert.Equal(
                expectedTimeline.Length,
                (await repeatedRebuild.GetSessionTimelineAsync(firstRun.SessionId, MonitoringScope)).Count);
            Assert.Equal(expectedAlarm, Assert.Single(
                await repeatedRebuild.GetAlarmsAsync(includeAcknowledged: true)));
            Assert.Equal(
                expectedLiveTimeline.Length,
                (await repeatedRebuild.GetSessionTimelineAsync(liveRun.SessionId, MonitoringScope)).Count);
        }
    }

    [Fact]
    public async Task ProjectionRejectsRuntimeEventPublishedBeforeDurableSessionSave()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        using var projection = new RuntimeMonitoringProjection(repository);
        await projection.InitializeAsync();
        var domainEvent = new RuntimeSessionCreatedDomainEvent(RuntimeSessionId.New());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await projection.HandleAsync([domainEvent]));

        Assert.Contains("before durable session persistence", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await projection.GetStationStatusesAsync(MonitoringScope));
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
            RuntimeSessionId.New(),
            new StationId(stationSystemId),
            new ConfigurationSnapshotId("snapshot-monitoring"),
            new RecipeSnapshotId("recipe-monitoring"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process-monitoring"),
                new ProcessVersionId("process-monitoring@1.0.0"),
                executableNodes),
            new Dictionary<string, ProductionContextValue>(),
            RuntimeTestReleaseIdentity.TraceMetadata(
                stationSystemId: stationSystemId,
                actorId: "runtime-monitoring-tests",
                projectId: "project-monitoring",
                applicationId: "application-monitoring",
                projectSnapshotId: "snapshot-monitoring",
                topologyId: "topology-monitoring"));
    }

    private static async Task<long> CountRowsAsync(string connectionString, string tableName)
    {
        var commandText = tableName switch
        {
            "runtime_monitoring_events" => "SELECT COUNT(*) FROM runtime_monitoring_events;",
            "runtime_monitoring_station_statuses" =>
                "SELECT COUNT(*) FROM runtime_monitoring_station_statuses;",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unknown test table.")
        };
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static StartRuntimeSessionRequest CreateScopedStartRequest(
        RuntimeMonitoringScope scope,
        string stationSystemId,
        params ExecutableRuntimeNode[] nodes)
    {
        return new StartRuntimeSessionRequest(
            RuntimeSessionId.New(),
            new StationId(stationSystemId),
            new ConfigurationSnapshotId($"configuration-{scope.ApplicationId}"),
            new RecipeSnapshotId($"recipe-{scope.ApplicationId}"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId($"process-{scope.ApplicationId}"),
                new ProcessVersionId($"process-{scope.ApplicationId}@1.0.0"),
                nodes),
            new Dictionary<string, ProductionContextValue>(),
            RuntimeTestReleaseIdentity.TraceMetadata(
                stationSystemId: stationSystemId,
                actorId: "runtime-monitoring-tests",
                projectId: scope.ProjectId,
                applicationId: scope.ApplicationId,
                projectSnapshotId: scope.ProjectSnapshotId,
                topologyId: scope.TopologyId));
    }

    private static StartRuntimeSessionRequest CreateProductionRunScopedStartRequest(
        RuntimeMonitoringScope scope,
        string stationSystemId,
        params ExecutableRuntimeNode[] nodes)
    {
        var productionRunId = scope.ProductionRunId
            ?? throw new ArgumentException("Production Run scope is required.", nameof(scope));
        return new StartRuntimeSessionRequest(
            RuntimeSessionId.New(),
            new StationId(stationSystemId),
            new ConfigurationSnapshotId("configuration-run-scope"),
            new RecipeSnapshotId("recipe-run-scope"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process-run-scope"),
                new ProcessVersionId("process-run-scope@1.0.0"),
                nodes),
            new Dictionary<string, ProductionContextValue>(),
            new RuntimeSessionTraceMetadata(
                productionRunId,
                ProductionUnitId.New(),
                "line-run-scope",
                "operation-run-scope",
                "operation-run-scope@0001",
                1,
                stationSystemId,
                new ProductionUnitIdentity(
                    "product-run-scope",
                    "serialNumber",
                    productionRunId.Value.ToString("D")),
                null,
                null,
                null,
                null,
                "runtime-monitoring-tests",
                scope.ProjectId,
                scope.ApplicationId,
                scope.ProjectSnapshotId,
                scope.TopologyId,
                RuntimeTestReleaseIdentity.ResourceFences(stationSystemId)));
    }

    private static ExecutableRuntimeNode TargetNode(
        string nodeId,
        string actionId,
        string targetKind,
        string targetId)
    {
        return new ExecutableRuntimeNode(
            new RuntimeNodeId(nodeId),
            nodeId,
            new RuntimeCapabilityId("system.run"),
            "Run",
            TimeSpan.FromSeconds(30),
            null,
            new RuntimeActionId(actionId),
            new RuntimeTargetReference(targetKind, targetId));
    }

    private static void AssertRunningTarget(
        RuntimeTargetStatusProjection status,
        string targetKind,
        string targetId)
    {
        Assert.Equal(targetKind, status.TargetKind);
        Assert.Equal(targetId, status.TargetId);
        Assert.Equal(MonitoringProductionRunId, status.ProductionRunId);
        Assert.Equal("line.main", status.ProductionLineDefinitionId);
        Assert.Equal("operation.main", status.OperationId);
        Assert.Equal(1, status.OperationAttempt);
        Assert.Equal("Station.System", status.StationSystemId);
        Assert.Equal("UNIT-DEFAULT", status.ProductionUnitIdentity.Value);
        Assert.Equal(ExecutionStatus.Running, status.CommandStatus);
        Assert.False(status.IsTerminal);
        Assert.Null(status.FailureReason);
    }

    private static void AssertTargetStatus(
        IEnumerable<RuntimeTargetStatusProjection> statuses,
        string targetKind,
        string targetId,
        ExecutionStatus expectedStatus,
        string? expectedFailureReason)
    {
        var status = Assert.Single(
            statuses,
            candidate => candidate.TargetKind == targetKind && candidate.TargetId == targetId);
        Assert.Equal(expectedStatus, status.CommandStatus);
        Assert.Equal(MonitoringProductionRunId, status.ProductionRunId);
        Assert.Equal("line.main", status.ProductionLineDefinitionId);
        Assert.Equal("operation.main", status.OperationId);
        Assert.Equal(1, status.OperationAttempt);
        Assert.Equal("Station.System", status.StationSystemId);
        Assert.Equal("UNIT-DEFAULT", status.ProductionUnitIdentity.Value);
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

        public List<RuntimeTargetStatusProjection> ObservedRunningStatuses { get; } = [];

        public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var current = Assert.Single(
                await _projection.GetTargetStatusesAsync(
                    MonitoringScope,
                    context.StationSystemId,
                    cancellationToken),
                status => status.TargetKind == context.TargetKind
                    && status.TargetId == context.TargetId);
            ObservedRunningStatuses.Add(current);

            return _results.Count == 0
                ? RuntimeCommandExecutionResult.Completed()
                : _results.Dequeue();
        }
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directoryPath, string connectionString)
        {
            DirectoryPath = directoryPath;
            ConnectionString = connectionString;
        }

        public string DirectoryPath { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "openlineops-runtime-monitoring-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directoryPath, "runtime.sqlite"),
                Pooling = false
            }.ToString();
            return new TemporarySqliteDatabase(directoryPath, connectionString);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
