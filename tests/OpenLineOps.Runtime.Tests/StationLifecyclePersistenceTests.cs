using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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
    private const string AgentId = "agent-a";
    private const string AgentInstanceId =
        "11111111-1111-4111-8111-111111111111";
    private const string LeaseHandle =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

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
        var facts = await repository.ListFactsAsync(stationId);
        Assert.Equal(2, facts.Count);
        Assert.Equal(0, facts[0].LifecycleRevision);
        Assert.Equal(1, facts[1].LifecycleRevision);
        Assert.Equal(facts[0].FactSha256, facts[1].PreviousFactSha256);
        Assert.Single(await repository.ListFactsAsync(
            stationId,
            afterSequence: 1,
            pageSize: 1));
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
            var facts = await verified.ListFactsAsync(stationId);
            Assert.Equal(3, facts.Count);
            Assert.Equal([1L, 2L, 3L], facts.Select(static fact => fact.Sequence));
            Assert.Equal(
                [0L, 1L, 2L],
                facts.Select(static fact => fact.LifecycleRevision));
            Assert.Equal(facts[0].FactSha256, facts[1].PreviousFactSha256);
            Assert.Equal(facts[1].FactSha256, facts[2].PreviousFactSha256);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE station_lifecycle_facts
                SET kind = 'Tampered'
                WHERE station_id = $station_id
                  AND sequence = 2;
                """;
            command.Parameters.AddWithValue("$station_id", stationId.Value);
            await Assert.ThrowsAsync<SqliteException>(async () =>
                await command.ExecuteNonQueryAsync());
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
        var handshakes = new InMemoryStationControllerHandshakeRepository();
        var clock = new MutableClock(BaseTimeUtc);
        var leases = new AlwaysCurrentAgentControlLease(clock);
        var service = new StationLifecycleService(
            repository,
            clock,
            handshakes,
            leases,
            leases,
            new TestStationRecipeStartAuthority(),
            HandshakeOptions());
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
        await AddReadyHandshakeAsync(
            handshakes,
            stationId,
            BaseTimeUtc.AddSeconds(2));
        await MoveToExecuteAsync(
            service,
            handshakes,
            stationId,
            clock);

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
        Assert.Equal(8, safetyLoss.Value.Revision);
        Assert.Equal(
            StationTransitionTrigger.SafetyPermitLost,
            safetyLoss.Value.Station.TransitionAudit[^1].Trigger);
    }

    [Fact]
    public async Task ServiceRetriesConflictsWithinBoundAndFailsAfterExactLimit()
    {
        var stationId = new StationId("station.retry");
        var retryingRepository = new ConflictInjectingRepository(failuresBeforeSuccess: 2);
        var retryingHandshakes =
            new InMemoryStationControllerHandshakeRepository();
        var retryingLeases = new AlwaysCurrentAgentControlLease(
            new FixedClock(BaseTimeUtc.AddSeconds(1)));
        var service = new StationLifecycleService(
            retryingRepository,
            new FixedClock(BaseTimeUtc.AddSeconds(1)),
            retryingHandshakes,
            retryingLeases,
            retryingLeases,
            new TestStationRecipeStartAuthority(),
            HandshakeOptions());
        Assert.True((await service.CreateAsync(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            "create retry station")).IsSuccess);
        await AddReadyHandshakeAsync(
            retryingHandshakes,
            stationId,
            BaseTimeUtc.AddSeconds(1));

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
        var failingHandshakes =
            new InMemoryStationControllerHandshakeRepository();
        var failingClock = new FixedClock(BaseTimeUtc.AddSeconds(1));
        var failingLeases = new AlwaysCurrentAgentControlLease(failingClock);
        var failingService = new StationLifecycleService(
            failingRepository,
            failingClock,
            failingHandshakes,
            failingLeases,
            failingLeases,
            new TestStationRecipeStartAuthority(),
            HandshakeOptions());
        Assert.True((await failingService.CreateAsync(
            failingStationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            "create exhausted station")).IsSuccess);
        await AddReadyHandshakeAsync(
            failingHandshakes,
            failingStationId,
            BaseTimeUtc.AddSeconds(1));

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

    [Fact]
    public async Task ExpiredCommandPersistsRecoveryIntentUntilCrossStoreSyncSucceeds()
    {
        var stationId = new StationId("station.command-expiry-recovery");
        var lifecycleRepository = new InMemoryStationLifecycleRepository();
        var handshakeRepository =
            new InMemoryStationControllerHandshakeRepository();
        var failingHandshakes = new SaveRejectingHandshakeRepository(
            handshakeRepository);
        var clock = new MutableClock(BaseTimeUtc);
        var options = new StationControllerHandshakeOptions
        {
            TimeToLive = TimeSpan.FromSeconds(30),
            MaximumSourceClockSkew = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(2)
        };
        var leases = new AlwaysCurrentAgentControlLease(clock);
        var service = new StationLifecycleService(
            lifecycleRepository,
            clock,
            failingHandshakes,
            leases,
            leases,
            new TestStationRecipeStartAuthority(),
            options);
        Assert.True((await service.CreateAsync(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            "create expiry station")).IsSuccess);
        await AddReadyHandshakeAsync(
            handshakeRepository,
            stationId,
            BaseTimeUtc);

        clock.UtcNow = BaseTimeUtc.AddSeconds(1);
        var reset = await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Reset,
            "operator-a",
            "issue reset");
        Assert.True(reset.IsSuccess);
        var command = Assert.IsType<StationControllerCommandExpectation>(
            reset.Value.Station.PendingControllerCommand);

        clock.UtcNow = command.DeadlineUtc;
        Assert.Equal(1, await service.ExpireControllerCommandsAsync());
        var interrupted = await lifecycleRepository.GetByIdAsync(stationId);
        Assert.NotNull(interrupted);
        Assert.Equal(StationState.Aborting, interrupted.Station.State);
        Assert.Null(interrupted.Station.PendingControllerCommand);
        var recovery = Assert.IsType<StationControllerRecoveryIntent>(
            interrupted.Station.PendingControllerRecovery);
        Assert.Equal(command.CommandId, recovery.CommandId);
        Assert.False(
            (await handshakeRepository.GetByIdAsync(stationId))!
                .State
                .RecoveryRequired);

        var restarted = new StationLifecycleService(
            lifecycleRepository,
            clock,
            handshakeRepository,
            leases,
            leases,
            new TestStationRecipeStartAuthority(),
            options);
        Assert.Equal(0, await restarted.ExpireControllerCommandsAsync());
        var synchronized = await lifecycleRepository.GetByIdAsync(stationId);
        Assert.NotNull(synchronized);
        Assert.Null(synchronized.Station.PendingControllerRecovery);
        Assert.True(
            (await handshakeRepository.GetByIdAsync(stationId))!
                .State
                .RecoveryRequired);
        Assert.Contains(
            await handshakeRepository.ListFactsAsync(stationId),
            static fact =>
                fact.Kind == StationControllerHandshakeFactKind.RecoveryRequired);
    }

    [Fact]
    public async Task AgentPollNeverReturnsCommandAtOrAfterServerDeadline()
    {
        var stationId = new StationId("station.expired-before-delivery");
        var lifecycleRepository = new InMemoryStationLifecycleRepository();
        var handshakeRepository =
            new InMemoryStationControllerHandshakeRepository();
        var clock = new MutableClock(BaseTimeUtc);
        var options = new StationControllerHandshakeOptions
        {
            TimeToLive = TimeSpan.FromMinutes(1),
            MaximumSourceClockSkew = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(2)
        };
        var leases = new AlwaysCurrentAgentControlLease(clock);
        var service = new StationLifecycleService(
            lifecycleRepository,
            clock,
            handshakeRepository,
            leases,
            leases,
            new TestStationRecipeStartAuthority(),
            options);
        Assert.True((await service.CreateAsync(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            "create deadline station")).IsSuccess);
        await AddReadyHandshakeAsync(
            handshakeRepository,
            stationId,
            BaseTimeUtc);

        clock.UtcNow = BaseTimeUtc.AddSeconds(1);
        var issued = await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Reset,
            "operator-a",
            "issue deadline command");
        Assert.True(issued.IsSuccess);
        var command = Assert.IsType<StationControllerCommandExpectation>(
            issued.Value.Station.PendingControllerCommand);
        clock.UtcNow = command.DeadlineUtc;

        var poll = await service.GetControllerCommandForAgentAsync(
            stationId,
            AgentId,
            AgentInstanceId,
            fencingToken: 1,
            LeaseHandle);

        Assert.True(poll.IsSuccess);
        Assert.Null(poll.Value.Station.PendingControllerCommand);
        Assert.Null(poll.Value.Station.PendingControllerCommandDelivery);
        Assert.Equal(StationState.Aborting, poll.Value.Station.State);
        Assert.True(
            (await handshakeRepository.GetByIdAsync(stationId))!
                .State
                .RecoveryRequired);
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
        InMemoryStationControllerHandshakeRepository handshakes,
        StationId stationId,
        MutableClock clock)
    {
        clock.UtcNow = BaseTimeUtc.AddSeconds(3);
        var reset = await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Reset,
            "operator-a",
            "reset");
        Assert.True(reset.IsSuccess);
        var resetCommand = Assert.IsType<StationControllerCommandExpectation>(
            reset.Value.Station.PendingControllerCommand);
        Assert.True((await service.GetControllerCommandForAgentAsync(
            stationId,
            AgentId,
            AgentInstanceId,
            resetCommand.FencingToken,
            LeaseHandle)).IsSuccess);
        clock.UtcNow = BaseTimeUtc.AddSeconds(4);
        var resetReport = await ReportControllerCompletionAsync(
            handshakes,
            stationId,
            resetCommand,
            clock.UtcNow);
        Assert.True((await service.AcknowledgeAsync(
            stationId,
            AgentInstanceId,
            resetCommand.FencingToken,
            LeaseHandle,
            resetCommand.CommandId,
            resetCommand.ControllerSessionId,
            resetCommand.ExpectedCommandSequence,
            resetReport.ObservedMode,
            resetReport.ObservedState,
            resetReport.StateSequence,
            AgentId,
            "reset complete")).IsSuccess);
        clock.UtcNow = BaseTimeUtc.AddSeconds(5);
        var start = await service.CommandAsync(
            stationId,
            StationLifecycleCommand.Start,
            "operator-a",
            "start");
        Assert.True(start.IsSuccess);
        var startCommand = Assert.IsType<StationControllerCommandExpectation>(
            start.Value.Station.PendingControllerCommand);
        Assert.True((await service.GetControllerCommandForAgentAsync(
            stationId,
            AgentId,
            AgentInstanceId,
            startCommand.FencingToken,
            LeaseHandle)).IsSuccess);
        clock.UtcNow = BaseTimeUtc.AddSeconds(6);
        var startReport = await ReportControllerCompletionAsync(
            handshakes,
            stationId,
            startCommand,
            clock.UtcNow);
        Assert.True((await service.AcknowledgeAsync(
            stationId,
            AgentInstanceId,
            startCommand.FencingToken,
            LeaseHandle,
            startCommand.CommandId,
            startCommand.ControllerSessionId,
            startCommand.ExpectedCommandSequence,
            startReport.ObservedMode,
            startReport.ObservedState,
            startReport.StateSequence,
            AgentId,
            "start complete")).IsSuccess);
    }

    private static async Task<StationControllerHandshakeReport>
        ReportControllerCompletionAsync(
        InMemoryStationControllerHandshakeRepository repository,
        StationId stationId,
        StationControllerCommandExpectation command,
        DateTimeOffset receivedAtUtc)
    {
        var entry = await repository.GetByIdAsync(stationId);
        Assert.NotNull(entry);
        var report = entry.State.Report(
            new StationControllerHandshakeReport(
                command.ControllerSessionId,
                entry.State.LatestReport.HeartbeatSequence + 1,
                command.ExpectedCommandSequence,
                command.ExpectedCommandSequence,
                busy: false,
                completed: true,
                error: false,
                errorCode: null,
                recipeConfirmed: true,
                confirmedRecipeId: "recipe-a",
                confirmedRecipeVersion: "1",
                safetyPermitGranted: true,
                sourceTimestampUtc: receivedAtUtc,
                receivedAtUtc,
                AgentId,
                AgentInstanceId,
                command.FencingToken,
                command.CommandId,
                command.FencingToken,
                StationMode.Automatic,
                command.Trigger == StationTransitionTrigger.Reset
                    ? StationState.Idle
                    : StationState.Execute,
                entry.State.LatestReport.StateSequence + 1));
        Assert.True(report.Succeeded);
        Assert.True(report.Changed);
        Assert.NotNull(report.FactKind);
        var fact = entry.State.CreateFact(
            entry.Revision + 2,
            report.FactKind.Value,
            "agent-a",
            "controller command complete",
            receivedAtUtc);
        _ = await repository.SaveAsync(
            entry.State,
            entry.Revision,
            fact);
        return entry.State.LatestReport;
    }

    private static async Task AddReadyHandshakeAsync(
        InMemoryStationControllerHandshakeRepository repository,
        StationId stationId,
        DateTimeOffset receivedAtUtc)
    {
        var state = StationControllerHandshake.Create(
            stationId,
            new StationControllerHandshakeReport(
                "controller-a",
                heartbeatSequence: 1,
                commandSequence: 0,
                acknowledgedCommandSequence: 0,
                busy: false,
                completed: false,
                error: false,
                errorCode: null,
                recipeConfirmed: true,
                confirmedRecipeId: "recipe-a",
                confirmedRecipeVersion: "1",
                safetyPermitGranted: true,
                sourceTimestampUtc: receivedAtUtc,
                receivedAtUtc,
                AgentId,
                AgentInstanceId,
                agentFencingToken: 1));
        Assert.True(await repository.TryAddAsync(
            state,
            state.CreateFact(
                1,
                StationControllerHandshakeFactKind.Reported,
                "agent-a",
                "controller ready",
                receivedAtUtc)));
    }

    private static StationControllerHandshakeOptions HandshakeOptions() =>
        new()
        {
            TimeToLive = TimeSpan.FromSeconds(30),
            MaximumSourceClockSkew = TimeSpan.FromSeconds(5)
        };

    private sealed class AlwaysCurrentAgentControlLease(IClock clock) :
        IStationAgentControlLeaseRepository,
        IStationAgentControlLeaseValidator
    {
        private static readonly string ProofSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.ASCII.GetBytes(LeaseHandle)))
            .ToLowerInvariant();

        public ValueTask<StationAgentControlLeaseObservation> GetAsync(
            StationId stationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedAtUtc = clock.UtcNow;
            return ValueTask.FromResult(new StationAgentControlLeaseObservation(
                Lease(stationId),
                observedAtUtc));
        }

        public ValueTask<StationAgentControlLeaseValidationResult>
            ValidateGenerationAsync(
                StationId stationId,
                string ownerAgentId,
                string ownerInstanceId,
                long fencingToken,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = new StationAgentControlLeaseObservation(
                Lease(stationId),
                clock.UtcNow);
            return ValueTask.FromResult(
                new StationAgentControlLeaseValidationResult(
                    observation.IsActive
                    && ownerAgentId == AgentId
                    && ownerInstanceId == AgentInstanceId
                    && fencingToken == 1,
                    observation));
        }

        public async ValueTask<StationAgentControlLeaseValidationResult>
            ValidateProofAsync(
                StationId stationId,
                string ownerAgentId,
                string ownerInstanceId,
                long fencingToken,
                string leaseProofSha256,
                CancellationToken cancellationToken = default)
        {
            var result = await ValidateGenerationAsync(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                cancellationToken);
            return result with
            {
                IsValid = result.IsValid
                    && string.Equals(
                        leaseProofSha256,
                        ProofSha256,
                        StringComparison.Ordinal)
            };
        }

        public ValueTask<StationAgentControlLeaseValidationResult> ValidateAsync(
            StationId stationId,
            string ownerAgentId,
            string ownerInstanceId,
            long fencingToken,
            string leaseHandle,
            CancellationToken cancellationToken = default) =>
            ValidateProofAsync(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.ASCII.GetBytes(leaseHandle)))
                    .ToLowerInvariant(),
                cancellationToken);

        public ValueTask<StationAgentControlLeaseMutationResult> TryAcquireAsync(
            StationId stationId,
            string ownerAgentId,
            string ownerInstanceId,
            string leaseProofSha256,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<StationAgentControlLeaseMutationResult> TryRenewAsync(
            StationId stationId,
            string ownerAgentId,
            string ownerInstanceId,
            long fencingToken,
            string leaseProofSha256,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<StationAgentControlLeaseMutationResult> TryReleaseAsync(
            StationId stationId,
            string ownerAgentId,
            string ownerInstanceId,
            long fencingToken,
            string leaseProofSha256,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static StationAgentControlLease Lease(StationId stationId) =>
            new(
                stationId,
                AgentId,
                AgentInstanceId,
                1,
                ProofSha256,
                BaseTimeUtc,
                BaseTimeUtc,
                BaseTimeUtc.AddHours(1));
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

        public ValueTask<IReadOnlyList<StationLifecyclePersistenceEntry>> ListAsync(
            CancellationToken cancellationToken = default) =>
            _inner.ListAsync(cancellationToken);

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

    private sealed class SaveRejectingHandshakeRepository(
        IStationControllerHandshakeRepository inner) :
        IStationControllerHandshakeRepository
    {
        public ValueTask<StationControllerHandshakePersistenceEntry?> GetByIdAsync(
            StationId stationId,
            CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(stationId, cancellationToken);

        public ValueTask<IReadOnlyList<StationControllerHandshakeFact>> ListFactsAsync(
            StationId stationId,
            CancellationToken cancellationToken = default) =>
            inner.ListFactsAsync(stationId, cancellationToken);

        public ValueTask<bool> TryAddAsync(
            StationControllerHandshake state,
            StationControllerHandshakeFact initialFact,
            CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(state, initialFact, cancellationToken);

        public ValueTask<long> SaveAsync(
            StationControllerHandshake state,
            long expectedRevision,
            StationControllerHandshakeFact? fact,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<long>(
                new StationControllerHandshakeConcurrencyException(
                    state.StationId,
                    expectedRevision));
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
