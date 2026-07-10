using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunRepositoryTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 10, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryRepositoryStoresACommittedSnapshotRatherThanLiveAggregateReference()
    {
        var repository = new InMemoryProductionRunRepository();
        var run = CreateRun("memory");
        Assert.True(await repository.TryAddAsync(run));

        Assert.True(run.Start(CreatedAtUtc.AddSeconds(1)).Succeeded);

        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id)).Run;
        Assert.Equal(ProductionRunStatus.Created, restored.Status);
        Assert.All(restored.Stages, stage => Assert.Equal(ProductionStageRunStatus.Pending, stage.Status));
    }

    [Fact]
    public async Task InMemoryRepositoryAtomicallyAcceptsOnlyOneRunWithTheSameId()
    {
        var repository = new InMemoryProductionRunRepository();
        var run = CreateRun("atomic-memory");

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => repository.TryAddAsync(run).AsTask()));

        Assert.Single(attempts, accepted => accepted);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task SqliteRepositoryAtomicallyRejectsDuplicateRunIdAndPreservesFirstDocument()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProductionRunRepository(database.ConnectionString);
        var first = CreateRun("atomic-sqlite");
        var duplicate = ProductionRun.Restore(first.ToSnapshot() with
        {
            DutIdentity = new DutIdentity("dut.model", "dut.serial", "DIFFERENT")
        });

        Assert.True(await repository.TryAddAsync(first));
        Assert.False(await repository.TryAddAsync(duplicate));

        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(first.Id)).Run;
        Assert.Equal("SN-atomic-sqlite", restored.DutIdentity.Value);
    }

    [Fact]
    public async Task RepositoriesRejectUpdatingRunThatWasNeverAdded()
    {
        var memory = new InMemoryProductionRunRepository();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await memory.SaveAsync(CreateRun("missing-memory"), 0));

        using var database = TemporarySqliteDatabase.Create();
        using var sqlite = new SqliteProductionRunRepository(database.ConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sqlite.SaveAsync(CreateRun("missing-sqlite"), 0));
    }

    [Fact]
    public async Task SqliteRepositoryRoundTripsCompleteFailedRunWithStageMetricsAndDutIdentity()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProductionRunRepository(database.ConnectionString);
        var run = CreateRun("sqlite");
        Assert.True(await repository.TryAddAsync(run));
        Assert.True(run.Start(CreatedAtUtc.AddSeconds(1)).Succeeded);
        var sessionId = RuntimeSessionId.New();
        Assert.True(run.StartStage(
            "stage.sqlite.1",
            sessionId,
            CreatedAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(run.FailStage(
            "stage.sqlite.1",
            "Runtime.InspectionFailed",
            "Inspection returned NG.",
            2,
            3,
            1,
            CreatedAtUtc.AddSeconds(3)).Succeeded);
        Assert.Equal(1, await repository.SaveAsync(run, 0));

        using var restartedRepository = new SqliteProductionRunRepository(database.ConnectionString);
        var restoredEntry = Assert.IsType<ProductionRunPersistenceEntry>(
            await restartedRepository.GetByIdAsync(run.Id));
        var restored = restoredEntry.Run;
        Assert.Equal(1, restoredEntry.Revision);

        Assert.Equal(ProductionRunStatus.Failed, restored.Status);
        Assert.Equal("dut.model", restored.DutIdentity.ModelId);
        Assert.Equal("dut.serial", restored.DutIdentity.InputKey);
        Assert.Equal("SN-sqlite", restored.DutIdentity.Value);
        Assert.Equal("operator.sqlite", restored.ActorId);
        Assert.Empty(restored.DomainEvents);
        Assert.Collection(
            restored.Stages,
            failed =>
            {
                Assert.Equal(ProductionStageRunStatus.Failed, failed.Status);
                Assert.Equal(sessionId, failed.RuntimeSessionId);
                Assert.Equal(2, failed.CompletedStepCount);
                Assert.Equal(3, failed.CommandCount);
                Assert.Equal(1, failed.IncidentCount);
            },
            skipped => Assert.Equal(ProductionStageRunStatus.Skipped, skipped.Status));
    }

    [Fact]
    public void SnapshotMapperRejectsNonCurrentSchemaAndIncompleteFormalIdentity()
    {
        var snapshot = ProductionRunSnapshotMapper.ToSnapshot(CreateRun("strict"));

        var schemaException = Assert.Throws<InvalidDataException>(() =>
            ProductionRunSnapshotMapper.ToAggregate(snapshot with { SchemaVersion = 0 }));
        Assert.Contains("schema is not current", schemaException.Message, StringComparison.Ordinal);

        var actorException = Assert.Throws<InvalidDataException>(() =>
            ProductionRunSnapshotMapper.ToAggregate(snapshot with { ActorId = null }));
        Assert.Contains("actor id", actorException.Message, StringComparison.Ordinal);

        var dutInputException = Assert.Throws<InvalidDataException>(() =>
            ProductionRunSnapshotMapper.ToAggregate(snapshot with { DutIdentityInputKey = null }));
        Assert.Contains("DUT identity input key", dutInputException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqliteRepositoryListsOnlyCreatedAndRunningRunsInTransitionOrder()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProductionRunRepository(database.ConnectionString);
        var created = CreateRun("created");
        var running = CreateRun("running");
        Assert.True(await repository.TryAddAsync(created));
        Assert.True(await repository.TryAddAsync(running));
        Assert.True(running.Start(CreatedAtUtc.AddMinutes(1)).Succeeded);
        var canceled = CreateRun("canceled");
        Assert.True(await repository.TryAddAsync(canceled));
        Assert.True(canceled.Cancel(
            "Canceled by operator.",
            0,
            0,
            0,
            CreatedAtUtc.AddMinutes(2)).Succeeded);
        await repository.SaveAsync(canceled, 0);
        await repository.SaveAsync(running, 0);
        await repository.SaveAsync(created, 0);

        var recoverable = await repository.ListRecoverableAsync();

        Assert.Collection(
            recoverable,
            first => Assert.Equal(created.Id, first.Run.Id),
            second => Assert.Equal(running.Id, second.Run.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryRejectsAStaleAggregateRevision(bool useSqlite)
    {
        using var database = TemporarySqliteDatabase.Create();
        using var sqlite = useSqlite
            ? new SqliteProductionRunRepository(database.ConnectionString)
            : null;
        IProductionRunRepository repository = sqlite is null
            ? new InMemoryProductionRunRepository()
            : sqlite;
        var run = CreateRun(useSqlite ? "cas-sqlite" : "cas-memory");
        Assert.True(await repository.TryAddAsync(run));
        var first = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id));
        var stale = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id));

        Assert.True(first.Run.Start(CreatedAtUtc.AddSeconds(1)).Succeeded);
        Assert.Equal(1, await repository.SaveAsync(first.Run, first.Revision));
        Assert.True(stale.Run.Cancel(
            "Stale caller canceled the run.",
            0,
            0,
            0,
            CreatedAtUtc.AddSeconds(2)).Succeeded);

        await Assert.ThrowsAsync<ProductionRunConcurrencyException>(async () =>
            await repository.SaveAsync(stale.Run, stale.Revision));

        var current = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id));
        Assert.Equal(1, current.Revision);
        Assert.Equal(ProductionRunStatus.Running, current.Run.Status);
        Assert.Empty(await repository.ListPendingTerminalOutboxAsync(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalSaveCreatesDurableOutboxUntilExplicitAcknowledgement(bool useSqlite)
    {
        using var database = TemporarySqliteDatabase.Create();
        using var sqlite = useSqlite
            ? new SqliteProductionRunRepository(database.ConnectionString)
            : null;
        IProductionRunRepository repository = sqlite is null
            ? new InMemoryProductionRunRepository()
            : sqlite;
        var run = CreateRun(useSqlite ? "outbox-sqlite" : "outbox-memory");
        Assert.True(await repository.TryAddAsync(run));
        Assert.True(run.Cancel(
            "Canceled before stage execution.",
            0,
            0,
            0,
            CreatedAtUtc.AddSeconds(1)).Succeeded);
        await repository.SaveAsync(run, 0);

        var pending = Assert.Single(await repository.ListPendingTerminalOutboxAsync(10));
        Assert.Equal(run.Id, pending.RunId);
        Assert.Equal(ProductionRunStatus.Canceled, pending.Run.Status);
        Assert.Equal(0, pending.AttemptCount);
        Assert.Null(pending.LastError);

        await repository.RecordTerminalOutboxFailureAsync(run.Id, "trace unavailable");
        var failed = Assert.Single(await repository.ListPendingTerminalOutboxAsync(10));
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal("trace unavailable", failed.LastError);

        await repository.MarkTerminalOutboxProcessedAsync(run.Id);
        Assert.Empty(await repository.ListPendingTerminalOutboxAsync(10));
    }

    private static ProductionRun CreateRun(string suffix)
    {
        return ProductionRun.Create(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            $"snapshot.{suffix}",
            "topology.main",
            "line.main",
            new DutIdentity("dut.model", "dut.serial", $"SN-{suffix}"),
            $"batch.{suffix}",
            null,
            null,
            $"operator.{suffix}",
            CreatedAtUtc,
            [
                Stage($"stage.{suffix}.1", 1),
                Stage($"stage.{suffix}.2", 2)
            ]);
    }

    private static ProductionStageRunDefinition Stage(string stageId, int sequence)
    {
        return new ProductionStageRunDefinition(
            stageId,
            sequence,
            $"workstation.{sequence}",
            new StationId($"station.{sequence}"),
            new ProcessDefinitionId($"process.{sequence}"),
            new ProcessVersionId($"process.{sequence}@1.0.0"),
            new ConfigurationSnapshotId($"configuration.{sequence}"),
            new RecipeSnapshotId($"recipe.{sequence}"));
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
            var directory = Path.Combine(
                Path.GetTempPath(),
                "OpenLineOps",
                Guid.NewGuid().ToString("N"));
            return new TemporarySqliteDatabase(
                directory,
                Path.Combine(directory, "production-runs.sqlite"));
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
