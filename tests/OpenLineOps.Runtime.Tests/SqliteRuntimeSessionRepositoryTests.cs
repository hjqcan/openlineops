using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Runtime.Domain.Targets;
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
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("node-inspect:action:1"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "system.inspect"));
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

    [Theory]
    [InlineData("$metadata")]
    [InlineData("projectId")]
    [InlineData("applicationId")]
    [InlineData("projectSnapshotId")]
    [InlineData("topologyId")]
    public async Task GetByIdAsyncRejectsPersistedSessionsWithoutCompleteReleaseIdentity(string missingField)
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var session = CreateRunningSession("missing-release-identity", BaseTimeUtc);
        await repository.SaveAsync(session);

        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT document_json FROM runtime_sessions WHERE session_id = $session_id;";
            select.Parameters.AddWithValue("$session_id", session.Id.Value.ToString("D"));
            var document = JsonNode.Parse((string)(await select.ExecuteScalarAsync())!)!.AsObject();
            if (string.Equals(missingField, "$metadata", StringComparison.Ordinal))
            {
                document["traceMetadata"] = null;
            }
            else
            {
                document["traceMetadata"]!.AsObject()[missingField] = null;
            }

            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE runtime_sessions SET document_json = $document WHERE session_id = $session_id;";
            update.Parameters.AddWithValue("$document", document.ToJsonString());
            update.Parameters.AddWithValue("$session_id", session.Id.Value.ToString("D"));
            await update.ExecuteNonQueryAsync();
        }

        using var restartedRepository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await restartedRepository.GetByIdAsync(session.Id));
    }

    [Fact]
    public async Task SaveAsyncRoundTripsRequiredActionAndTargetIdentity()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var session = CreateRunningSession("semantic-identity", BaseTimeUtc);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("inspect"),
            "Inspect",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("inspect:action:1"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "system.tester"));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("vision.inspect"),
            "Inspect",
            BaseTimeUtc.AddSeconds(3),
            TimeSpan.FromSeconds(5));

        await repository.SaveAsync(session);

        using var restartedRepository = new SqliteRuntimeSessionRepository(database.ConnectionString);
        var restored = Assert.IsType<RuntimeSession>(await restartedRepository.GetByIdAsync(session.Id));
        var restoredStep = Assert.Single(restored.Steps);
        var restoredCommand = Assert.Single(restored.Commands);
        Assert.Equal("inspect:action:1", restoredStep.ActionId.Value);
        Assert.Equal(RuntimeTargetKinds.System, restoredStep.TargetKind);
        Assert.Equal("system.tester", restoredStep.TargetId);
        Assert.Equal(restoredStep.ActionId, restoredCommand.ActionId);
        Assert.Equal(restoredStep.Target, restoredCommand.Target);
        Assert.Equal(command.Id, restoredCommand.Id);
    }

    [Fact]
    public void SnapshotMapperRejectsSnapshotWithoutActionIdentity()
    {
        var snapshot = RuntimeSessionSnapshotMapper.ToSnapshot(
            CreateSessionWithCommand("missing-action"));
        var invalidSnapshot = snapshot with
        {
            Steps = snapshot.Steps
                .Select(candidate => candidate with { ActionId = null! })
                .ToArray()
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => RuntimeSessionSnapshotMapper.ToAggregate(invalidSnapshot));

        Assert.Contains("ActionId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotMapperRejectsSnapshotWithoutTargetIdentity()
    {
        var snapshot = RuntimeSessionSnapshotMapper.ToSnapshot(
            CreateSessionWithCommand("missing-target"));
        var invalidSnapshot = snapshot with
        {
            Commands = snapshot.Commands
                .Select(candidate => candidate with { TargetKind = null!, TargetId = null! })
                .ToArray()
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => RuntimeSessionSnapshotMapper.ToAggregate(invalidSnapshot));

        Assert.Contains("TargetKind and TargetId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotMapperRejectsCommandIdentityThatDiffersFromOwningStep()
    {
        var snapshot = RuntimeSessionSnapshotMapper.ToSnapshot(
            CreateSessionWithCommand("mismatched-target"));
        var invalidSnapshot = snapshot with
        {
            Commands = snapshot.Commands
                .Select(candidate => candidate with { TargetId = "system.other" })
                .ToArray()
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => RuntimeSessionSnapshotMapper.ToAggregate(invalidSnapshot));

        Assert.Contains("semantic identity differs", exception.Message, StringComparison.Ordinal);
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

    private static RuntimeSession CreateSessionWithCommand(string suffix)
    {
        var session = CreateRunningSession(suffix, BaseTimeUtc);
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("inspect"),
            "Inspect",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("inspect:action:1"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "system.tester"));
        session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("vision.inspect"),
            "Inspect",
            BaseTimeUtc.AddSeconds(3),
            TimeSpan.FromSeconds(5));
        return session;
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
            traceMetadata ?? RuntimeTestReleaseIdentity.TraceMetadata());

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
