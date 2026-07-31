using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class StationProductionExecutionGateTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 2, 0, 0, TimeSpan.Zero);
    private const string AgentA = "agent-a";
    private const string OwnerInstanceA =
        "11111111-1111-4111-8111-111111111111";
    private const string OwnerInstanceB =
        "22222222-2222-4222-8222-222222222222";
    private const string LeaseProofSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task UnenrolledProductionStationFailsClosedAndLegacyTestGateIsExplicit()
    {
        var clock = new FixedClock(Now);
        var gate = new StationProductionExecutionGate(
            new InMemoryStationLifecycleRepository(),
            new InMemoryStationControllerHandshakeRepository(),
            new InMemoryStationAgentControlLeaseRepository(clock),
            clock,
            Options());

        var result = await gate.EvaluateAsync("station-unmanaged");

        Assert.False(result.Managed);
        Assert.False(result.Allowed);
        Assert.Null(result.Evidence);

        var released = await gate.EvaluateAsync(
            "station-unmanaged",
            new StationExecutionRecipeExpectation("recipe-a", "1"));
        Assert.False(released.Managed);
        Assert.False(released.Allowed);
        Assert.Contains("not enrolled", released.Reason, StringComparison.Ordinal);

        var legacy = await LegacyCompatibilityStationProductionExecutionGate.Instance
            .EvaluateAsync("station-unmanaged");
        Assert.False(legacy.Managed);
        Assert.True(legacy.Allowed);
        Assert.Null(legacy.Evidence);
    }

    [Fact]
    public async Task ManagedStoppedStationBlocksProductionDispatch()
    {
        var repository = new InMemoryStationLifecycleRepository();
        var station = StationLifecycle.Create(
            new StationId("station-managed"),
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "agent-a",
            Now);
        Assert.True(await repository.TryAddAsync(station));
        var clock = new FixedClock(Now);
        var gate = new StationProductionExecutionGate(
            repository,
            new InMemoryStationControllerHandshakeRepository(),
            new InMemoryStationAgentControlLeaseRepository(clock),
            clock,
            Options());

        var result = await gate.EvaluateAsync("station-managed");

        Assert.True(result.Managed);
        Assert.False(result.Allowed);
        Assert.Contains("not Execute", result.Reason, StringComparison.Ordinal);
        Assert.NotNull(result.Evidence);
    }

    [Fact]
    public async Task ManagedExecuteStationRequiresAllReadinessAndProducesRevisionEvidence()
    {
        var repository = new InMemoryStationLifecycleRepository();
        var station = StationLifecycle.Create(
            new StationId("station-execute"),
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "agent-a",
            Now);
        var authorization = new StationCommandAuthorization(
            "agent-a",
            StationCommandGrant.All);
        Assert.True(station.Reset(authorization, "Prepare station.", Now).Succeeded);
        Assert.True(
            station.AcknowledgeTransition(
                authorization,
                "Reset completed.",
                Now.AddMilliseconds(1)).Succeeded);
        Assert.True(
            station.Start(
                authorization,
                "Start automatic execution.",
                Now.AddMilliseconds(2)).Succeeded);
        Assert.True(
            station.AcknowledgeTransition(
                authorization,
                "Start completed.",
                Now.AddMilliseconds(3)).Succeeded);
        Assert.True(await repository.TryAddAsync(station));
        var handshakes = new InMemoryStationControllerHandshakeRepository();
        await RegisterReadyHandshakeAsync(
            handshakes,
            station.Id,
            Now.AddMilliseconds(3));
        var clock = new FixedClock(Now.AddMilliseconds(3));
        var gate = new StationProductionExecutionGate(
            repository,
            handshakes,
            await CreateActiveLeaseRepositoryAsync(station.Id, clock),
            clock,
            Options());

        var result = await gate.EvaluateAsync("station-execute");

        Assert.True(result.Managed);
        Assert.True(result.Allowed);
        Assert.Contains(
            "station-lifecycle:station-execute:0:Automatic:Execute",
            result.Evidence,
            StringComparison.Ordinal);
        Assert.NotNull(result.AgentControlLease);
        Assert.Contains(
            "station-gate-revision:v1:station-execute:0:",
            result.Revision,
            StringComparison.Ordinal);
        Assert.Equal(AgentA, result.AgentControlLease.OwnerAgentId);
        Assert.Equal(OwnerInstanceA, result.AgentControlLease.OwnerInstanceId);
        Assert.Equal(1, result.AgentControlLease.FencingToken);
        Assert.Contains(
            "|agent-control-lease:agent-a:11111111-1111-4111-8111-111111111111:1:",
            result.Evidence,
            StringComparison.Ordinal);

        var matchingRecipe = await gate.EvaluateAsync(
            "station-execute",
            new StationExecutionRecipeExpectation("recipe-a", "3"));
        var mismatchedRecipe = await gate.EvaluateAsync(
            "station-execute",
            new StationExecutionRecipeExpectation("recipe-a", "4"));
        Assert.True(matchingRecipe.Allowed);
        Assert.False(mismatchedRecipe.Allowed);
        Assert.Contains(
            "expected recipe-a/4",
            mismatchedRecipe.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedSimulationStationCanRunButMaintenanceStationCannot()
    {
        var simulation = await CreateExecutingStationAsync(
            "station-simulation",
            StationMode.Simulation);
        var maintenance = await CreateExecutingStationAsync(
            "station-maintenance",
            StationMode.Maintenance);

        Assert.True((await simulation.EvaluateAsync("station-simulation")).Allowed);
        Assert.False((await maintenance.EvaluateAsync("station-maintenance")).Allowed);
    }

    [Fact]
    public async Task ManagedExecuteStationBlocksMissingAndStaleControllerHeartbeat()
    {
        var repository = await CreateExecutingLifecycleAsync(
            "station-controller-gate",
            StationMode.Automatic);
        var handshakes = new InMemoryStationControllerHandshakeRepository();
        var currentClock = new FixedClock(Now);
        var gateWithoutReport = new StationProductionExecutionGate(
            repository,
            handshakes,
            new InMemoryStationAgentControlLeaseRepository(currentClock),
            currentClock,
            Options());

        var missing = await gateWithoutReport.EvaluateAsync("station-controller-gate");

        Assert.False(missing.Allowed);
        Assert.Contains(
            "has not been reported",
            missing.Reason,
            StringComparison.Ordinal);

        await RegisterReadyHandshakeAsync(
            handshakes,
            new StationId("station-controller-gate"),
            Now);
        var staleClock = new FixedClock(Now.AddSeconds(6));
        var staleGate = new StationProductionExecutionGate(
            repository,
            handshakes,
            new InMemoryStationAgentControlLeaseRepository(staleClock),
            staleClock,
            Options());

        var stale = await staleGate.EvaluateAsync("station-controller-gate");

        Assert.False(stale.Allowed);
        Assert.Contains("expired", stale.Reason, StringComparison.Ordinal);
        Assert.Contains("controller-handshake:0:", stale.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleChangeBetweenRepositoryReadsNeverReturnsStaleAuthorization()
    {
        var stationId = new StationId("station-torn-read");
        var executingRepository = await CreateExecutingLifecycleAsync(
            stationId.Value,
            StationMode.Automatic);
        var executing = (await executingRepository.GetByIdAsync(stationId))!;
        var stopped = StationLifecycle.Create(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "agent-a",
            Now.AddMilliseconds(4));
        var switchingRepository = new SwitchingLifecycleRepository(
            executing,
            new StationLifecyclePersistenceEntry(stopped, executing.Revision + 1));
        var handshakes = new InMemoryStationControllerHandshakeRepository();
        await RegisterReadyHandshakeAsync(handshakes, stationId, Now.AddMilliseconds(3));
        var clock = new FixedClock(Now.AddMilliseconds(4));
        var gate = new StationProductionExecutionGate(
            switchingRepository,
            handshakes,
            await CreateActiveLeaseRepositoryAsync(stationId, clock),
            clock,
            Options());

        var result = await gate.EvaluateAsync(stationId.Value);

        Assert.False(result.Allowed);
        Assert.Contains("not Execute", result.Reason, StringComparison.Ordinal);
        Assert.True(switchingRepository.ReadCount >= 3);
    }

    [Fact]
    public async Task AgentControlLeaseTakeoverBetweenReadsNeverReturnsStaleAuthorization()
    {
        var stationId = new StationId("station-lease-race");
        var lifecycle = await CreateExecutingLifecycleAsync(
            stationId.Value,
            StationMode.Automatic);
        var handshakes = new InMemoryStationControllerHandshakeRepository();
        await RegisterReadyHandshakeAsync(
            handshakes,
            stationId,
            Now.AddMilliseconds(3));
        var original = ControlLease(
            stationId,
            AgentA,
            OwnerInstanceA,
            fencingToken: 1);
        var takeover = ControlLease(
            stationId,
            "agent-b",
            OwnerInstanceB,
            fencingToken: 2);
        var leases = new SwitchingControlLeaseRepository(
            original,
            takeover,
            Now.AddMilliseconds(4));
        var gate = new StationProductionExecutionGate(
            lifecycle,
            handshakes,
            leases,
            new FixedClock(Now.AddMilliseconds(4)),
            Options());

        var result = await gate.EvaluateAsync(stationId.Value);

        Assert.False(result.Allowed);
        Assert.Contains(
            "does not match the active Station Agent control lease",
            result.Reason,
            StringComparison.Ordinal);
        Assert.True(leases.ReadCount >= 2);
    }

    private static async Task<StationProductionExecutionGate> CreateExecutingStationAsync(
        string stationId,
        StationMode mode)
    {
        var repository = await CreateExecutingLifecycleAsync(stationId, mode);
        var handshakes = new InMemoryStationControllerHandshakeRepository();
        await RegisterReadyHandshakeAsync(
            handshakes,
            new StationId(stationId),
            Now.AddMilliseconds(3),
            mode);
        var clock = new FixedClock(Now.AddMilliseconds(3));
        return new StationProductionExecutionGate(
            repository,
            handshakes,
            await CreateActiveLeaseRepositoryAsync(
                new StationId(stationId),
                clock),
            clock,
            Options());
    }

    private static async Task<InMemoryStationLifecycleRepository>
        CreateExecutingLifecycleAsync(
            string stationId,
            StationMode mode)
    {
        var repository = new InMemoryStationLifecycleRepository();
        var station = StationLifecycle.Create(
            new StationId(stationId),
            mode,
            StationReadiness.ReadyToExecute,
            "agent-a",
            Now);
        var authorization = new StationCommandAuthorization(
            "agent-a",
            StationCommandGrant.All);
        station.Reset(authorization, "Prepare station.", Now);
        station.AcknowledgeTransition(
            authorization,
            "Reset completed.",
            Now.AddMilliseconds(1));
        station.Start(
            authorization,
            "Start execution.",
            Now.AddMilliseconds(2));
        station.AcknowledgeTransition(
            authorization,
            "Start completed.",
            Now.AddMilliseconds(3));
        Assert.True(await repository.TryAddAsync(station));
        return repository;
    }

    private static async Task RegisterReadyHandshakeAsync(
        InMemoryStationControllerHandshakeRepository repository,
        StationId stationId,
        DateTimeOffset receivedAtUtc,
        StationMode mode = StationMode.Automatic)
    {
        var state = StationControllerHandshake.Create(
            stationId,
            ReadyReport(receivedAtUtc, mode));
        Assert.True(await repository.TryAddAsync(
            state,
            state.CreateFact(
                1,
                StationControllerHandshakeFactKind.Reported,
                "agent-a",
                "controller online",
                receivedAtUtc)));
    }

    private static StationControllerHandshakeReport ReadyReport(
        DateTimeOffset receivedAtUtc,
        StationMode mode) =>
        new(
            "controller-session-a",
            heartbeatSequence: 1,
            commandSequence: 7,
            acknowledgedCommandSequence: 7,
            busy: false,
            completed: true,
            error: false,
            errorCode: null,
            recipeConfirmed: true,
            confirmedRecipeId: "recipe-a",
            confirmedRecipeVersion: "3",
            safetyPermitGranted: true,
            sourceTimestampUtc: receivedAtUtc,
            receivedAtUtc,
            ownerAgentId: AgentA,
            ownerAgentInstanceId: OwnerInstanceA,
            agentFencingToken: 1,
            commandId: "command-7",
            commandFencingToken: 7,
            observedMode: mode,
            observedState: StationState.Execute,
            stateSequence: 4);

    private static async Task<InMemoryStationAgentControlLeaseRepository>
        CreateActiveLeaseRepositoryAsync(
            StationId stationId,
            IClock clock)
    {
        var repository = new InMemoryStationAgentControlLeaseRepository(clock);
        var acquisition = await repository.TryAcquireAsync(
            stationId,
            AgentA,
            OwnerInstanceA,
            LeaseProofSha256,
            TimeSpan.FromSeconds(30));
        Assert.Equal(
            StationAgentControlLeaseMutationStatus.Acquired,
            acquisition.Status);
        return repository;
    }

    private static StationAgentControlLease ControlLease(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken) =>
        new(
            stationId,
            ownerAgentId,
            ownerInstanceId,
            fencingToken,
            LeaseProofSha256,
            Now,
            Now,
            Now.AddSeconds(30));

    private static StationControllerHandshakeOptions Options() =>
        new()
        {
            TimeToLive = TimeSpan.FromSeconds(5),
            MaximumSourceClockSkew = TimeSpan.FromSeconds(1)
        };

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed class SwitchingLifecycleRepository(
        StationLifecyclePersistenceEntry first,
        StationLifecyclePersistenceEntry current) : IStationLifecycleRepository
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask<StationLifecyclePersistenceEntry?> GetByIdAsync(
            StationId stationId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(first.Station.Id, stationId);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<StationLifecyclePersistenceEntry?>(
                Interlocked.Increment(ref _readCount) == 1 ? first : current);
        }

        public ValueTask<bool> TryAddAsync(
            StationLifecycle station,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<long> SaveAsync(
            StationLifecycle station,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<StationLifecyclePersistenceEntry>> ListAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SwitchingControlLeaseRepository(
        StationAgentControlLease first,
        StationAgentControlLease current,
        DateTimeOffset observedAtUtc) : IStationAgentControlLeaseRepository
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask<StationAgentControlLeaseObservation> GetAsync(
            StationId stationId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(first.StationId, stationId);
            cancellationToken.ThrowIfCancellationRequested();
            var lease = Interlocked.Increment(ref _readCount) == 1
                ? first
                : current;
            return ValueTask.FromResult(
                new StationAgentControlLeaseObservation(lease, observedAtUtc));
        }

        public ValueTask<StationAgentControlLeaseValidationResult>
            ValidateGenerationAsync(
                StationId stationId,
                string ownerAgentId,
                string ownerInstanceId,
                long fencingToken,
                CancellationToken cancellationToken = default)
        {
            Assert.Equal(first.StationId, stationId);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            return ValueTask.FromResult(new StationAgentControlLeaseValidationResult(
                current.IsOwnedBy(ownerAgentId, ownerInstanceId)
                    && current.FencingToken == fencingToken,
                new StationAgentControlLeaseObservation(current, observedAtUtc)));
        }

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

        public ValueTask<StationAgentControlLeaseValidationResult> ValidateProofAsync(
            StationId stationId,
            string ownerAgentId,
            string ownerInstanceId,
            long fencingToken,
            string leaseProofSha256,
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
    }
}
