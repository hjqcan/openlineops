using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class SqliteRuntimeSessionRepositoryTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SaveAsyncPersistsRuntimeSessionGraphForNewRepositoryInstance()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var session = CreateRunningSession("roundtrip", BaseTimeUtc);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node-inspect"),
            "Inspect",
            BaseTimeUtc.AddSeconds(2));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("vision-camera"),
            "Inspect",
            BaseTimeUtc.AddSeconds(3),
            TimeSpan.FromSeconds(30));
        session.AcceptCommand(command.Id, BaseTimeUtc.AddSeconds(4));
        session.StartCommand(command.Id, BaseTimeUtc.AddSeconds(5));
        session.CompleteCommand(command.Id, "{\"ok\":true}", BaseTimeUtc.AddSeconds(6));
        session.CompleteStep(step.Id, BaseTimeUtc.AddSeconds(7));
        var incident = session.RecordIncident(
            RuntimeIncidentSeverity.Warning,
            "Runtime.CameraTemperatureHigh",
            "Camera temperature is above nominal range.",
            BaseTimeUtc.AddSeconds(8));

        await repository.SaveAsync(session);

        using var restartedRepository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(session.Id);

        Assert.NotNull(restored);
        Assert.Equal(session.Id, restored.Id);
        Assert.Equal(RuntimeSessionStatus.Running, restored.Status);
        Assert.Equal("snapshot-roundtrip", restored.ConfigurationSnapshotId.Value);
        Assert.Equal(BaseTimeUtc.AddSeconds(1), restored.StartedAtUtc);
        Assert.Empty(restored.DomainEvents);

        var restoredStep = Assert.Single(restored.Steps);
        Assert.Equal(step.Id, restoredStep.Id);
        Assert.Equal(RuntimeStepStatus.Completed, restoredStep.Status);
        Assert.Equal(BaseTimeUtc.AddSeconds(7), restoredStep.CompletedAtUtc);

        var restoredCommand = Assert.Single(restored.Commands);
        Assert.Equal(command.Id, restoredCommand.Id);
        Assert.Equal(RuntimeCommandStatus.Completed, restoredCommand.Status);
        Assert.Equal("{\"ok\":true}", restoredCommand.ResultPayload);
        Assert.Equal(BaseTimeUtc.AddSeconds(6), restoredCommand.CompletedAtUtc);

        var restoredIncident = Assert.Single(restored.Incidents);
        Assert.Equal(incident.Id, restoredIncident.Id);
        Assert.Equal(RuntimeIncidentSeverity.Warning, restoredIncident.Severity);
        Assert.Equal("Runtime.CameraTemperatureHigh", restoredIncident.Code);
    }

    [Fact]
    public async Task SaveAsyncPersistsRuntimeTraceMetadataForNewRepositoryInstance()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var session = CreateRunningSession(
            "trace-metadata",
            BaseTimeUtc,
            new RuntimeSessionTraceMetadata(
                "SN-TRACE-001",
                "BATCH-TRACE",
                "FIXTURE-TRACE",
                "DEVICE-TRACE",
                "OPERATOR-TRACE",
                "PROJECT-TRACE",
                "APPLICATION-TRACE",
                "PROJECT-SNAPSHOT-TRACE",
                "TOPOLOGY-TRACE"));

        await repository.SaveAsync(session);

        using var restartedRepository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(session.Id);

        Assert.NotNull(restored);
        Assert.Equal("SN-TRACE-001", restored.TraceMetadata.SerialNumber);
        Assert.Equal("BATCH-TRACE", restored.TraceMetadata.BatchId);
        Assert.Equal("FIXTURE-TRACE", restored.TraceMetadata.FixtureId);
        Assert.Equal("DEVICE-TRACE", restored.TraceMetadata.DeviceId);
        Assert.Equal("OPERATOR-TRACE", restored.TraceMetadata.ActorId);
        Assert.Equal("PROJECT-TRACE", restored.TraceMetadata.ProjectId);
        Assert.Equal("APPLICATION-TRACE", restored.TraceMetadata.ApplicationId);
        Assert.Equal("PROJECT-SNAPSHOT-TRACE", restored.TraceMetadata.ProjectSnapshotId);
        Assert.Equal("TOPOLOGY-TRACE", restored.TraceMetadata.TopologyId);
    }

    [Fact]
    public async Task SaveAsyncRoundTripsDynamicStepAndActionIdentity()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var session = CreateRunningSession("dynamic-identity", BaseTimeUtc);
        var parent = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("script-node"),
            "Script",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("script-node:action:1"));
        var child = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("script-node:slot:node:4"),
            "Child",
            BaseTimeUtc.AddSeconds(3),
            new RuntimeActionId("script-node:action:1:child:4"),
            parent.Id,
            dynamicSequence: 4);
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            child.Id,
            new RuntimeCapabilityId("motion.axis"),
            "MoveAxis",
            BaseTimeUtc.AddSeconds(4),
            TimeSpan.FromSeconds(5));

        await repository.SaveAsync(session);

        using var restartedRepository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var restored = Assert.IsType<RuntimeSession>(await restartedRepository.GetByIdAsync(session.Id));
        var restoredChild = restored.Steps.Single(step => step.Id == child.Id);
        var restoredCommand = Assert.Single(restored.Commands);
        Assert.Equal("script-node:action:1:child:4", restoredChild.ActionId.Value);
        Assert.Equal(parent.Id, restoredChild.ParentStepId);
        Assert.Equal(4, restoredChild.DynamicSequence);
        Assert.Equal(restoredChild.ActionId, restoredCommand.ActionId);
        Assert.Equal(command.Id, restoredCommand.Id);
    }

    [Fact]
    public void SnapshotMapperDerivesLegacyCommandActionIdentityFromOwningStep()
    {
        var session = CreateRunningSession("legacy-action", BaseTimeUtc);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("legacy-node"),
            "Legacy",
            BaseTimeUtc.AddSeconds(2));
        session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("legacy.capability"),
            "LegacyCommand",
            BaseTimeUtc.AddSeconds(3),
            TimeSpan.FromSeconds(5));
        var snapshot = RuntimeSessionSnapshotMapper.ToSnapshot(session);
        var legacySnapshot = snapshot with
        {
            Steps = snapshot.Steps
                .Select(candidate => candidate with { ActionId = null })
                .ToArray(),
            Commands = snapshot.Commands
                .Select(candidate => candidate with { ActionId = null })
                .ToArray()
        };

        var restored = RuntimeSessionSnapshotMapper.ToAggregate(legacySnapshot);

        var restoredStep = Assert.Single(restored.Steps);
        var restoredCommand = Assert.Single(restored.Commands);
        Assert.Equal("legacy-node:action:1", restoredStep.ActionId.Value);
        Assert.Equal(restoredStep.ActionId, restoredCommand.ActionId);
    }

    [Fact]
    public async Task ListRecoverableAsyncReturnsOnlyNonTerminalSessionsInRecoveryOrder()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var running = CreateRunningSession("running", BaseTimeUtc.AddMinutes(1));
        var paused = CreateRunningSession("paused", BaseTimeUtc.AddMinutes(3));
        paused.RequestPause(BaseTimeUtc.AddMinutes(4), "operator pause");
        paused.ConfirmPaused(BaseTimeUtc.AddMinutes(5), "paused");
        var completed = CreateRunningSession("completed", BaseTimeUtc.AddMinutes(6));
        completed.Complete(BaseTimeUtc.AddMinutes(7));

        await repository.SaveAsync(paused);
        await repository.SaveAsync(completed);
        await repository.SaveAsync(running);

        var service = new RuntimeSessionRecoveryService(repository);
        var plan = await service.CreateRecoveryPlanAsync();

        Assert.Equal(2, plan.Count);
        Assert.Collection(
            plan.Candidates,
            first =>
            {
                Assert.Equal(running.Id, first.SessionId);
                Assert.Equal(RuntimeSessionStatus.Running, first.Status);
            },
            second =>
            {
                Assert.Equal(paused.Id, second.SessionId);
                Assert.Equal(RuntimeSessionStatus.Paused, second.Status);
            });
    }

    [Fact]
    public async Task GetByIdAsyncReturnsNullForMissingRuntimeSession()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteRuntimeSessionRepository(database.ConnectionString);

        var session = await repository.GetByIdAsync(RuntimeSessionId.New());

        Assert.Null(session);
    }

    private static RuntimeSession CreateRunningSession(
        string suffix,
        DateTimeOffset createdAtUtc,
        RuntimeSessionTraceMetadata? traceMetadata = null)
    {
        var session = RuntimeSession.Create(
            RuntimeSessionId.New(),
            new StationId($"station-{suffix}"),
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId($"process-packaging@{suffix}"),
            new ConfigurationSnapshotId($"snapshot-{suffix}"),
            new RecipeSnapshotId($"recipe-{suffix}"),
            createdAtUtc,
            traceMetadata);

        var result = session.Start(createdAtUtc.AddSeconds(1));
        Assert.True(result.Succeeded, result.Message);

        return session;
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        public string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "runtime-sessions.sqlite");

            return new TemporarySqliteDatabase(directory, databasePath);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
