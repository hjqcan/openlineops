using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class StationLifecyclePersistenceTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly StationCommandAuthorization Authorization =
        new("operator-a", StationCommandGrant.All);

    [Fact]
    public async Task InMemoryRepositoryUsesDetachedSnapshotsAndOptimisticRevision()
    {
        var repository = new InMemoryStationLifecycleRepository();
        var stationId = new StationId("station.repository");
        var station = CreateStation(stationId);

        Assert.True(await repository.TryAddAsync(station));
        Assert.Empty(station.DomainEvents);
        var first = await repository.GetByIdAsync(stationId);
        var stale = await repository.GetByIdAsync(stationId);
        Assert.NotNull(first);
        Assert.NotNull(stale);
        Assert.NotSame(first.Station, stale.Station);

        Assert.True(first.Station.Reset(
            Authorization,
            "prepare first writer",
            BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(stale.Station.Reset(
            Authorization,
            "prepare stale writer",
            BaseTimeUtc.AddSeconds(1)).Succeeded);

        Assert.Equal(1, await repository.SaveAsync(first.Station, first.Revision));
        await Assert.ThrowsAsync<StationLifecycleConcurrencyException>(async () =>
            await repository.SaveAsync(stale.Station, stale.Revision));

        var persisted = await repository.GetByIdAsync(stationId);
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted.Revision);
        Assert.Equal(StationState.Resetting, persisted.Station.State);
        var transition = Assert.Single(persisted.Station.TransitionAudit);
        Assert.Equal("prepare first writer", transition.Reason);
    }

    [Fact]
    public async Task SqliteRepositorySurvivesRestartWithExactTransitionAudit()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-station-lifecycle-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "runtime.sqlite");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var stationId = new StationId("station.sqlite");

        try
        {
            using (var repository = new SqliteStationLifecycleRepository(connectionString))
            {
                Assert.True(await repository.TryAddAsync(CreateStation(stationId)));
                var entry = await repository.GetByIdAsync(stationId);
                Assert.NotNull(entry);
                Assert.True(entry.Station.Reset(
                    Authorization,
                    "durable reset",
                    BaseTimeUtc.AddSeconds(1)).Succeeded);
                Assert.Equal(1, await repository.SaveAsync(entry.Station, entry.Revision));
            }

            using (var restarted = new SqliteStationLifecycleRepository(connectionString))
            {
                var entry = await restarted.GetByIdAsync(stationId);
                Assert.NotNull(entry);
                Assert.Equal(1, entry.Revision);
                Assert.Equal(StationState.Resetting, entry.Station.State);
                var transition = Assert.Single(entry.Station.TransitionAudit);
                Assert.Equal(StationTransitionTrigger.Reset, transition.Trigger);
                Assert.Equal("durable reset", transition.Reason);
                Assert.True(entry.Station.AcknowledgeTransition(
                    Authorization,
                    "reset completed after restart",
                    BaseTimeUtc.AddSeconds(2)).Succeeded);
                Assert.Equal(2, await restarted.SaveAsync(entry.Station, entry.Revision));
            }

            using var verified = new SqliteStationLifecycleRepository(connectionString);
            var persisted = await verified.GetByIdAsync(stationId);
            Assert.NotNull(persisted);
            Assert.Equal(2, persisted.Revision);
            Assert.Equal(StationState.Idle, persisted.Station.State);
            Assert.Equal(2, persisted.Station.TransitionAudit.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SqliteRepositoryRejectsUnmappedSnapshotFields()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-station-lifecycle-json-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "runtime.sqlite");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var stationId = new StationId("station.strict-json");

        try
        {
            using var repository = new SqliteStationLifecycleRepository(connectionString);
            Assert.True(await repository.TryAddAsync(CreateStation(stationId)));
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE station_lifecycles
                    SET document_json =
                        substr(document_json, 1, length(document_json) - 1)
                        || ',"unexpected":true}'
                    WHERE station_id = $station_id;
                    """;
                command.Parameters.AddWithValue("$station_id", stationId.Value);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await Assert.ThrowsAsync<JsonException>(async () =>
                await repository.GetByIdAsync(stationId));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ServiceReturnsDomainFailuresAndPersistsSafetyPermitAbort()
    {
        var repository = new InMemoryStationLifecycleRepository();
        var clock = new MutableClock(BaseTimeUtc);
        var service = new StationLifecycleService(repository, clock);
        var stationId = new StationId("station.service");
        var create = await service.CreateAsync(
            stationId,
            StationMode.Automatic,
            StationReadiness.NotReady,
            "engineer-a",
            "commission station");
        Assert.True(create.IsSuccess);

        clock.UtcNow = BaseTimeUtc.AddSeconds(1);
        var rejected = await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Reset,
            "operator-a",
            "prepare station");
        Assert.True(rejected.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.StationPrerequisitesNotSatisfied",
            rejected.Error.Code);

        clock.UtcNow = BaseTimeUtc.AddSeconds(2);
        Assert.True((await service.UpdateReadinessAsync(
            stationId,
            StationReadiness.ReadyToExecute,
            "agent-a",
            "all prerequisites verified")).IsSuccess);
        await MoveToExecuteAsync(service, stationId, clock);

        clock.UtcNow = BaseTimeUtc.AddSeconds(7);
        var unsafeReadiness = StationReadiness.ReadyToExecute with
        {
            SafetyPermitGranted = false
        };
        var safetyLoss = await service.UpdateReadinessAsync(
            stationId,
            unsafeReadiness,
            "agent-a",
            "guard opened");

        Assert.True(safetyLoss.IsSuccess);
        Assert.Equal(StationState.Aborting, safetyLoss.Value.Station.State);
        Assert.Equal(6, safetyLoss.Value.Revision);
        Assert.Equal(
            StationTransitionTrigger.SafetyPermitLost,
            safetyLoss.Value.Station.TransitionAudit[^1].Trigger);
    }

    [Fact]
    public async Task ServiceRetriesConflictsWithinBoundAndFailsAfterExactLimit()
    {
        var stationId = new StationId("station.retry");
        var retryingRepository = new ConflictInjectingRepository(failuresBeforeSuccess: 2);
        var service = new StationLifecycleService(
            retryingRepository,
            new FixedClock(BaseTimeUtc.AddSeconds(1)));
        Assert.True((await service.CreateAsync(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            "create retry station")).IsSuccess);

        var reset = await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Reset,
            "operator-a",
            "retry reset");

        Assert.True(reset.IsSuccess);
        Assert.Equal(3, retryingRepository.SaveAttempts);
        Assert.Equal(1, reset.Value.Revision);

        var failingStationId = new StationId("station.retry-exhausted");
        var failingRepository = new ConflictInjectingRepository(int.MaxValue);
        var failingService = new StationLifecycleService(
            failingRepository,
            new FixedClock(BaseTimeUtc.AddSeconds(1)));
        Assert.True((await failingService.CreateAsync(
            failingStationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            "create exhausted station")).IsSuccess);

        var exhausted = await failingService.CommandAsync(
            failingStationId,
            StationLifecycleCommand.Reset,
            "operator-a",
            "exhaust retries");

        Assert.True(exhausted.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.StationLifecycleConcurrencyConflict",
            exhausted.Error.Code);
        Assert.Equal(
            StationLifecycleService.MaximumConcurrencyAttempts,
            failingRepository.SaveAttempts);
    }

    private static StationLifecycle CreateStation(StationId stationId)
    {
        return StationLifecycle.Create(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            BaseTimeUtc);
    }

    private static async Task MoveToExecuteAsync(
        StationLifecycleService service,
        StationId stationId,
        MutableClock clock)
    {
        clock.UtcNow = BaseTimeUtc.AddSeconds(3);
        Assert.True((await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Reset,
            "operator-a",
            "reset")).IsSuccess);
        clock.UtcNow = BaseTimeUtc.AddSeconds(4);
        Assert.True((await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Acknowledge,
            "agent-a",
            "reset complete")).IsSuccess);
        clock.UtcNow = BaseTimeUtc.AddSeconds(5);
        Assert.True((await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Start,
            "operator-a",
            "start")).IsSuccess);
        clock.UtcNow = BaseTimeUtc.AddSeconds(6);
        Assert.True((await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Acknowledge,
            "agent-a",
            "start complete")).IsSuccess);
    }

    private sealed class ConflictInjectingRepository(int failuresBeforeSuccess) :
        IStationLifecycleRepository
    {
        private readonly InMemoryStationLifecycleRepository _inner = new();
        private int _remainingFailures = failuresBeforeSuccess;

        public int SaveAttempts { get; private set; }

        public ValueTask<bool> TryAddAsync(
            StationLifecycle station,
            CancellationToken cancellationToken = default) =>
            _inner.TryAddAsync(station, cancellationToken);

        public ValueTask<StationLifecyclePersistenceEntry?> GetByIdAsync(
            StationId stationId,
            CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(stationId, cancellationToken);

        public ValueTask<long> SaveAsync(
            StationLifecycle station,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveAttempts++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new StationLifecycleConcurrencyException(
                    station.Id,
                    expectedRevision);
            }

            return _inner.SaveAsync(station, expectedRevision, cancellationToken);
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
