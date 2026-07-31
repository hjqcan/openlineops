using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class StationControllerHandshakeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private const string InstanceA = "11111111-1111-4111-8111-111111111111";
    private const string InstanceB = "22222222-2222-4222-8222-222222222222";
    private static readonly string LeaseHandleA = CreateLeaseHandle(0x11);
    private static readonly string LeaseHandleB = CreateLeaseHandle(0x22);

    [Fact]
    public void DomainEnforcesCoherentControllerStateAndIdempotentSequences()
    {
        Assert.Throws<ArgumentException>(() => new StationControllerHandshakeReport(
            "controller-a",
            heartbeatSequence: 1,
            commandSequence: 1,
            acknowledgedCommandSequence: 1,
            busy: true,
            completed: true,
            error: false,
            errorCode: null,
            recipeConfirmed: true,
            confirmedRecipeId: "recipe-a",
            confirmedRecipeVersion: "1",
            safetyPermitGranted: true,
            sourceTimestampUtc: Now,
            receivedAtUtc: Now,
            ownerAgentId: AgentA,
            ownerAgentInstanceId: InstanceA,
            agentFencingToken: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadyReport(
            "controller-a",
            heartbeatSequence: 1,
            commandSequence: 1,
            acknowledgedCommandSequence: 2,
            Now));

        var state = StationControllerHandshake.Create(
            new StationId("station-domain"),
            ReadyReport("controller-a", 7, 4, 4, Now));
        var replay = state.Report(ReadyReport(
            "controller-a",
            7,
            4,
            4,
            Now,
            receivedAtUtc: Now.AddSeconds(1)));
        var conflictingReplay = state.Report(new StationControllerHandshakeReport(
            "controller-a",
            heartbeatSequence: 7,
            commandSequence: 4,
            acknowledgedCommandSequence: 4,
            busy: false,
            completed: false,
            error: false,
            errorCode: null,
            recipeConfirmed: true,
            confirmedRecipeId: "recipe-a",
            confirmedRecipeVersion: "1",
            safetyPermitGranted: true,
            sourceTimestampUtc: Now,
            receivedAtUtc: Now.AddSeconds(1),
            ownerAgentId: AgentA,
            ownerAgentInstanceId: InstanceA,
            agentFencingToken: 1,
            commandId: state.LatestReport.CommandId,
            commandFencingToken: state.LatestReport.CommandFencingToken,
            observedMode: state.LatestReport.ObservedMode,
            observedState: state.LatestReport.ObservedState,
            stateSequence: state.LatestReport.StateSequence));
        var sessionChange = state.Report(ReadyReport(
            "controller-b",
            1,
            0,
            0,
            Now.AddSeconds(2)));

        Assert.True(replay.Succeeded);
        Assert.False(replay.Changed);
        Assert.False(conflictingReplay.Succeeded);
        Assert.Equal(
            "Runtime.StationControllerHandshakeIdempotencyConflict",
            conflictingReplay.Code);
        Assert.True(sessionChange.Succeeded);
        Assert.True(sessionChange.Changed);
        Assert.True(state.RecoveryRequired);
        Assert.Equal(
            StationControllerHandshakeFactKind.ControllerSessionChanged,
            sessionChange.FactKind);
        var delayedRetiredSession = state.Report(ReadyReport(
            "controller-a",
            8,
            5,
            5,
            Now.AddSeconds(3)));
        Assert.False(delayedRetiredSession.Succeeded);
        Assert.Equal(
            "Runtime.StationControllerHandshakeRetiredSession",
            delayedRetiredSession.Code);
    }

    [Fact]
    public async Task ServiceFencesSessionRecoveryAndStartUntilAuthorizedAcknowledgement()
    {
        var lifecycleRepository = new InMemoryStationLifecycleRepository();
        var handshakeRepository =
            new InMemoryStationControllerHandshakeRepository();
        var stationId = new StationId("station-service-handshake");
        Assert.True(await lifecycleRepository.TryAddAsync(StationLifecycle.Create(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            Now)));
        var clock = new MutableClock(Now);
        var (leaseRepository, leaseService) =
            await CreateActiveLeaseAsync(clock, stationId);
        var options = Options(TimeSpan.FromSeconds(30));
        var handshakeService = new StationControllerHandshakeService(
            handshakeRepository,
            lifecycleRepository,
            leaseRepository,
            leaseService,
            clock,
            options);

        var first = await handshakeService.ReportAsync(
            stationId,
            ReadyInput(
                "controller-a",
                1,
                0,
                0,
                Now),
            AgentA,
            "controller connected");
        clock.UtcNow = Now.AddSeconds(1);
        var replay = await handshakeService.ReportAsync(
            stationId,
            ReadyInput(
                "controller-a",
                1,
                0,
                0,
                Now),
            AgentA,
            "delivery replay");

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(0, replay.Value.Revision);
        Assert.Single(await handshakeRepository.ListFactsAsync(stationId));

        var lifecycleService = new StationLifecycleService(
            lifecycleRepository,
            clock,
            handshakeRepository,
            leaseRepository,
            leaseService,
            new TestStationRecipeStartAuthority(),
            options);
        var reset = await lifecycleService.CommandAsync(
            stationId,
            StationLifecycleCommand.Reset,
            "operator-a",
            "reset station");
        Assert.True(reset.IsSuccess);
        var resetCommand = Assert.IsType<StationControllerCommandExpectation>(
            reset.Value.Station.PendingControllerCommand);
        Assert.True((await lifecycleService.GetControllerCommandForAgentAsync(
            stationId,
            AgentA,
            InstanceA,
            resetCommand.FencingToken,
            LeaseHandleA)).IsSuccess);
        clock.UtcNow = Now.AddSeconds(2);
        Assert.True((await handshakeService.ReportAsync(
            stationId,
            ReadyInput(
                "controller-a",
                2,
                resetCommand.ExpectedCommandSequence,
                resetCommand.ExpectedCommandSequence,
                clock.UtcNow,
                resetCommand.CommandId,
                resetCommand.FencingToken,
                StationState.Idle,
                stateSequence: 2),
            AgentA,
            "reset controller command completed")).IsSuccess);
        var staleAcknowledgement = await lifecycleService.AcknowledgeAsync(
            stationId,
            InstanceA,
            agentFencingToken: 1,
            LeaseHandleA,
            resetCommand.CommandId,
            resetCommand.ControllerSessionId,
            resetCommand.ExpectedCommandSequence,
            StationMode.Automatic,
            StationState.Idle,
            stateSequence: 1,
            AgentA,
            "stale state evidence");
        Assert.True(staleAcknowledgement.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.StationControllerCommandIncomplete",
            staleAcknowledgement.Error.Code);
        Assert.True((await lifecycleService.AcknowledgeAsync(
            stationId,
            InstanceA,
            agentFencingToken: 1,
            LeaseHandleA,
            resetCommand.CommandId,
            resetCommand.ControllerSessionId,
            resetCommand.ExpectedCommandSequence,
            StationMode.Automatic,
            StationState.Idle,
            stateSequence: 2,
            AgentA,
            "reset complete")).IsSuccess);

        clock.UtcNow = Now.AddSeconds(3);
        var changedSession = await handshakeService.ReportAsync(
            stationId,
            ReadyInput(
                "controller-b",
                1,
                0,
                0,
                clock.UtcNow,
                observedState: StationState.Idle),
            AgentA,
            "controller process restarted");
        Assert.True(changedSession.IsSuccess);
        Assert.True(changedSession.Value.State.RecoveryRequired);

        clock.UtcNow = Now.AddSeconds(4);
        var blockedStart = await lifecycleService.CommandAsync(
            stationId,
            StationLifecycleCommand.Start,
            "operator-a",
            "attempt start before recovery");
        Assert.True(blockedStart.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.StationControllerHandshakeRecoveryRequired",
            blockedStart.Error.Code);

        clock.UtcNow = Now.AddSeconds(5);
        var recoveryEpoch = changedSession.Value.State.RecoveryEpoch;
        var operationalStateSha256 =
            StationControllerHandshakeEvidence.OperationalStateSha256(
                changedSession.Value.State);
        var acknowledgement = await handshakeService.AcknowledgeRecoveryAsync(
            stationId,
            recoveryEpoch,
            "controller-b",
            operationalStateSha256,
            "safety-a",
            "physical station state verified");
        Assert.True(acknowledgement.IsSuccess);
        Assert.False(acknowledgement.Value.State.RecoveryRequired);

        clock.UtcNow = Now.AddSeconds(6);
        var started = await lifecycleService.CommandAsync(
            stationId,
            StationLifecycleCommand.Start,
            "operator-a",
            "start after recovery");
        Assert.True(started.IsSuccess);
        Assert.Equal(StationState.Starting, started.Value.Station.State);

        var facts = await handshakeRepository.ListFactsAsync(stationId);
        Assert.Equal(4, facts.Count);
        Assert.Equal(
            [
                StationControllerHandshakeFactKind.Reported,
                StationControllerHandshakeFactKind.Reported,
                StationControllerHandshakeFactKind.ControllerSessionChanged,
                StationControllerHandshakeFactKind.RecoveryAcknowledged
            ],
            facts.Select(static fact => fact.Kind));
        Assert.Equal([1L, 2L, 3L, 4L], facts.Select(static fact => fact.Sequence));
    }

    [Fact]
    public async Task ServiceRejectsUnenrolledStationAndOutOfOrderOrConflictingReports()
    {
        var lifecycleRepository = new InMemoryStationLifecycleRepository();
        var handshakeRepository =
            new InMemoryStationControllerHandshakeRepository();
        var clock = new MutableClock(Now);
        var stationId = new StationId("station-report-order");
        var (leaseRepository, leaseService) =
            await CreateActiveLeaseAsync(clock, stationId);
        var service = new StationControllerHandshakeService(
            handshakeRepository,
            lifecycleRepository,
            leaseRepository,
            leaseService,
            clock,
            Options(TimeSpan.FromSeconds(10)));

        var unenrolled = await service.ReportAsync(
            stationId,
            ReadyInput("controller-a", 1, 0, 0, Now),
            AgentA,
            "rogue report");
        Assert.True(unenrolled.IsFailure);
        Assert.Equal("NotFound.Runtime.StationLifecycleNotFound", unenrolled.Error.Code);

        Assert.True(await lifecycleRepository.TryAddAsync(StationLifecycle.Create(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            Now)));
        Assert.True((await service.ReportAsync(
            stationId,
            ReadyInput("controller-a", 2, 1, 1, Now),
            AgentA,
            "first observed report")).IsSuccess);

        clock.UtcNow = Now.AddSeconds(1);
        var older = await service.ReportAsync(
            stationId,
            ReadyInput("controller-a", 1, 1, 1, Now),
            AgentA,
            "delayed report");
        var conflict = await service.ReportAsync(
            stationId,
            ReadyInput("controller-a", 2, 2, 2, Now),
            AgentA,
            "same heartbeat changed payload");

        Assert.Equal(
            "Conflict.Runtime.StationControllerHandshakeOutOfOrder",
            older.Error.Code);
        Assert.Equal(
            "Conflict.Runtime.StationControllerHandshakeIdempotencyConflict",
            conflict.Error.Code);
        Assert.Single(await handshakeRepository.ListFactsAsync(stationId));
    }

    [Fact]
    public async Task ServiceRejectsSourceClockSkewBeforePersistingAnyState()
    {
        var lifecycleRepository = new InMemoryStationLifecycleRepository();
        var handshakeRepository =
            new InMemoryStationControllerHandshakeRepository();
        var stationId = new StationId("station-source-clock-skew");
        Assert.True(await lifecycleRepository.TryAddAsync(StationLifecycle.Create(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            Now)));
        var clock = new MutableClock(Now);
        var (leaseRepository, leaseService) =
            await CreateActiveLeaseAsync(clock, stationId);
        var service = new StationControllerHandshakeService(
            handshakeRepository,
            lifecycleRepository,
            leaseRepository,
            leaseService,
            clock,
            Options(TimeSpan.FromSeconds(30)));

        var rejected = await service.ReportAsync(
            stationId,
            ReadyInput(
                "controller-a",
                1,
                0,
                0,
                Now.AddMinutes(1)),
            AgentA,
            "future source timestamp");

        Assert.True(rejected.IsFailure);
        Assert.Equal(
            "Validation.Runtime.StationControllerHandshakeClockSkew",
            rejected.Error.Code);
        Assert.Null(await handshakeRepository.GetByIdAsync(stationId));
        Assert.Empty(await handshakeRepository.ListFactsAsync(stationId));

        var accepted = await service.ReportAsync(
            stationId,
            ReadyInput("controller-a", 1, 0, 0, Now),
            AgentA,
            "valid source timestamp");
        Assert.True(accepted.IsSuccess);
        Assert.Equal(0, accepted.Value.Revision);
    }

    [Fact]
    public async Task RecoveryIntentRejectsAnOperationalStateChangedAfterReview()
    {
        var lifecycleRepository = new InMemoryStationLifecycleRepository();
        var handshakeRepository =
            new InMemoryStationControllerHandshakeRepository();
        var stationId = new StationId("station-stale-recovery-intent");
        Assert.True(await lifecycleRepository.TryAddAsync(StationLifecycle.Create(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            Now)));
        var clock = new MutableClock(Now);
        var (leaseRepository, leaseService) =
            await CreateActiveLeaseAsync(clock, stationId);
        var service = new StationControllerHandshakeService(
            handshakeRepository,
            lifecycleRepository,
            leaseRepository,
            leaseService,
            clock,
            Options(TimeSpan.FromSeconds(30)));
        Assert.True((await service.ReportAsync(
            stationId,
            ReadyInput("controller-a", 1, 0, 0, Now),
            AgentA,
            "initial session")).IsSuccess);
        clock.UtcNow = Now.AddSeconds(1);
        var changed = await service.ReportAsync(
            stationId,
            ReadyInput("controller-b", 1, 0, 0, clock.UtcNow),
            AgentA,
            "controller restarted");
        Assert.True(changed.IsSuccess);
        var reviewedHash =
            StationControllerHandshakeEvidence.OperationalStateSha256(
                changed.Value.State);

        clock.UtcNow = Now.AddSeconds(2);
        var safetyChanged = await service.ReportAsync(
            stationId,
            ReadyInput(
                    "controller-b",
                    2,
                    0,
                    0,
                    clock.UtcNow)
                with
                {
                    SafetyPermitGranted = false
                },
            AgentA,
            "safety permit changed after review");
        Assert.True(safetyChanged.IsSuccess);

        clock.UtcNow = Now.AddSeconds(3);
        var acknowledgement = await service.AcknowledgeRecoveryAsync(
            stationId,
            changed.Value.State.RecoveryEpoch,
            "controller-b",
            reviewedHash,
            "safety-a",
            "attempt stale recovery acknowledgement");

        Assert.True(acknowledgement.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.StationControllerHandshakeRecoveryIntentStale",
            acknowledgement.Error.Code);
    }

    [Fact]
    public async Task StateSequenceGapRequiresExplicitRecovery()
    {
        var lifecycleRepository = new InMemoryStationLifecycleRepository();
        var handshakeRepository =
            new InMemoryStationControllerHandshakeRepository();
        var stationId = new StationId("station-state-sequence-gap");
        Assert.True(await lifecycleRepository.TryAddAsync(StationLifecycle.Create(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            Now)));
        var clock = new MutableClock(Now);
        var (leaseRepository, leaseService) =
            await CreateActiveLeaseAsync(clock, stationId);
        var service = new StationControllerHandshakeService(
            handshakeRepository,
            lifecycleRepository,
            leaseRepository,
            leaseService,
            clock,
            Options(TimeSpan.FromSeconds(30)));

        var first = await service.ReportAsync(
            stationId,
            ReadyInput(
                "controller-a",
                heartbeatSequence: 1,
                commandSequence: 0,
                acknowledgedCommandSequence: 0,
                Now,
                stateSequence: 1),
            AgentA,
            "initial controller state");
        clock.UtcNow = Now.AddSeconds(1);
        var gap = await service.ReportAsync(
            stationId,
            ReadyInput(
                "controller-a",
                heartbeatSequence: 2,
                commandSequence: 0,
                acknowledgedCommandSequence: 0,
                clock.UtcNow,
                stateSequence: 3),
            AgentA,
            "state sequence skipped an observation");

        Assert.True(first.IsSuccess);
        Assert.True(gap.IsSuccess);
        Assert.True(gap.Value.State.RecoveryRequired);
        Assert.Equal(1, gap.Value.State.RecoveryEpoch);
        Assert.Equal(3, gap.Value.State.LatestReport.StateSequence);
        var facts = await handshakeRepository.ListFactsAsync(stationId);
        Assert.Equal(2, facts.Count);
        Assert.Equal(
            StationControllerHandshakeFactKind.RecoveryRequired,
            facts[^1].Kind);
        Assert.Contains(
            "intermediate state evidence is missing",
            facts[^1].Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeaseTakeoverRejectsPreviousOwnerReport()
    {
        var lifecycleRepository = new InMemoryStationLifecycleRepository();
        var handshakeRepository =
            new InMemoryStationControllerHandshakeRepository();
        var stationId = new StationId("station-lease-takeover-report");
        Assert.True(await lifecycleRepository.TryAddAsync(StationLifecycle.Create(
            stationId,
            StationMode.Automatic,
            StationReadiness.ReadyToExecute,
            "engineer-a",
            Now)));
        var clock = new MutableClock(Now);
        var (leaseRepository, leaseService) =
            await CreateActiveLeaseAsync(clock, stationId);
        var service = new StationControllerHandshakeService(
            handshakeRepository,
            lifecycleRepository,
            leaseRepository,
            leaseService,
            clock,
            Options(TimeSpan.FromSeconds(30)));

        Assert.True((await service.ReportAsync(
            stationId,
            ReadyInput("controller-a", 1, 0, 0, Now),
            AgentA,
            "initial owner report")).IsSuccess);
        clock.UtcNow = Now.AddSeconds(1);
        Assert.True((await leaseService.ReleaseAsync(
            stationId,
            AgentA,
            InstanceA,
            fencingToken: 1,
            LeaseHandleA)).IsSuccess);
        var takeover = await leaseService.AcquireAsync(
            stationId,
            AgentB,
            InstanceB,
            LeaseHandleB);
        Assert.True(takeover.IsSuccess);
        Assert.Equal(2, takeover.Value.Observation.Lease!.FencingToken);

        var staleReport = await service.ReportAsync(
            stationId,
            ReadyInput(
                "controller-a",
                heartbeatSequence: 2,
                commandSequence: 0,
                acknowledgedCommandSequence: 0,
                clock.UtcNow),
            AgentA,
            "previous owner delayed report");

        Assert.True(staleReport.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.StationAgentControlLeaseInvalid",
            staleReport.Error.Code);
        var unchanged = await handshakeRepository.GetByIdAsync(stationId);
        Assert.NotNull(unchanged);
        Assert.Equal(1, unchanged.State.LatestReport.HeartbeatSequence);
        Assert.Single(await handshakeRepository.ListFactsAsync(stationId));
        Assert.False((await leaseService.ValidateAsync(
            stationId,
            AgentA,
            InstanceA,
            fencingToken: 1,
            LeaseHandleA)).IsValid);

        var currentOwnerReport = await service.ReportAsync(
            stationId,
            ReadyInput(
                "controller-a",
                heartbeatSequence: 2,
                commandSequence: 0,
                acknowledgedCommandSequence: 0,
                clock.UtcNow,
                ownerAgentInstanceId: InstanceB,
                agentFencingToken: 2,
                leaseHandle: LeaseHandleB),
            AgentB,
            "takeover owner report");
        Assert.True(currentOwnerReport.IsSuccess);
        Assert.True(currentOwnerReport.Value.State.RecoveryRequired);
    }

    [Fact]
    public async Task SqliteStateAndAppendOnlyFactsSurviveColdRestart()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-controller-handshake-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "runtime.sqlite");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var stationId = new StationId("station-handshake-sqlite");

        try
        {
            var clock = new MutableClock(Now);
            var (leaseRepository, leaseService) =
                await CreateActiveLeaseAsync(clock, stationId);
            using (var lifecycleRepository =
                   new SqliteStationLifecycleRepository(connectionString))
            using (var handshakeRepository =
                   new SqliteStationControllerHandshakeRepository(connectionString))
            {
                Assert.True(await lifecycleRepository.TryAddAsync(
                    StationLifecycle.Create(
                        stationId,
                        StationMode.Automatic,
                        StationReadiness.ReadyToExecute,
                        "engineer-a",
                        Now)));
                var service = new StationControllerHandshakeService(
                    handshakeRepository,
                    lifecycleRepository,
                    leaseRepository,
                    leaseService,
                    clock,
                    Options(TimeSpan.FromSeconds(30)));
                Assert.True((await service.ReportAsync(
                    stationId,
                    ReadyInput("controller-a", 1, 0, 0, Now) with
                    {
                        Completed = false
                    },
                    AgentA,
                    "initial controller report")).IsSuccess);
                clock.UtcNow = Now.AddSeconds(1);
                Assert.True((await service.ReportAsync(
                    stationId,
                    ReadyInput("controller-a", 2, 0, 0, clock.UtcNow) with
                    {
                        Completed = false
                    },
                    AgentA,
                    "heartbeat-only controller report")).IsSuccess);
                clock.UtcNow = Now.AddSeconds(2);
                Assert.True((await service.ReportAsync(
                    stationId,
                    ReadyInput("controller-a", 3, 0, 0, clock.UtcNow),
                    AgentA,
                    "next operational controller report")).IsSuccess);
            }

            using var restarted =
                new SqliteStationControllerHandshakeRepository(connectionString);
            var persisted = await restarted.GetByIdAsync(stationId);
            var facts = await restarted.ListFactsAsync(stationId);

            Assert.NotNull(persisted);
            Assert.Equal(2, persisted.Revision);
            Assert.Equal(3, persisted.State.LatestReport.HeartbeatSequence);
            Assert.Equal(0, persisted.State.LatestReport.CommandSequence);
            Assert.Equal(2, facts.Count);
            Assert.Equal([1L, 3L], facts.Select(static fact => fact.Sequence));
            Assert.Equal(
                "next operational controller report",
                facts[^1].Reason);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static StationControllerHandshakeInput ReadyInput(
        string sessionId,
        long heartbeatSequence,
        long commandSequence,
        long acknowledgedCommandSequence,
        DateTimeOffset sourceTimestampUtc,
        string? commandId = null,
        long? commandFencingToken = null,
        StationState observedState = StationState.Stopped,
        long stateSequence = 1,
        string ownerAgentInstanceId = InstanceA,
        long agentFencingToken = 1,
        string? leaseHandle = null) =>
        new(
            ownerAgentInstanceId,
            agentFencingToken,
            leaseHandle ?? LeaseHandleA,
            sessionId,
            heartbeatSequence,
            commandSequence,
            acknowledgedCommandSequence,
            Busy: false,
            Completed: true,
            Error: false,
            ErrorCode: null,
            RecipeConfirmed: true,
            ConfirmedRecipeId: "recipe-a",
            ConfirmedRecipeVersion: "1",
            SafetyPermitGranted: true,
            sourceTimestampUtc,
            commandSequence == 0
                ? null
                : commandId ?? $"historical-{sessionId}-{commandSequence}",
            commandSequence == 0
                ? 0
                : commandFencingToken ?? commandSequence,
            StationMode.Automatic,
            observedState,
            stateSequence);

    private static StationControllerHandshakeReport ReadyReport(
        string sessionId,
        long heartbeatSequence,
        long commandSequence,
        long acknowledgedCommandSequence,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset? receivedAtUtc = null,
        string? commandId = null,
        long? commandFencingToken = null,
        StationState observedState = StationState.Stopped,
        long stateSequence = 1,
        string ownerAgentId = AgentA,
        string ownerAgentInstanceId = InstanceA,
        long agentFencingToken = 1) =>
        new(
            sessionId,
            heartbeatSequence,
            commandSequence,
            acknowledgedCommandSequence,
            busy: false,
            completed: true,
            error: false,
            errorCode: null,
            recipeConfirmed: true,
            confirmedRecipeId: "recipe-a",
            confirmedRecipeVersion: "1",
            safetyPermitGranted: true,
            sourceTimestampUtc,
            receivedAtUtc ?? sourceTimestampUtc,
            ownerAgentId,
            ownerAgentInstanceId,
            agentFencingToken,
            commandSequence == 0
                ? null
                : commandId ?? $"historical-{sessionId}-{commandSequence}",
            commandSequence == 0
                ? 0
                : commandFencingToken ?? commandSequence,
            StationMode.Automatic,
            observedState,
            stateSequence);

    private static StationControllerHandshakeOptions Options(TimeSpan timeToLive) =>
        new()
        {
            TimeToLive = timeToLive,
            MaximumSourceClockSkew = TimeSpan.FromSeconds(5)
        };

    private static async Task<(
        InMemoryStationAgentControlLeaseRepository Repository,
        StationAgentControlLeaseService Service)> CreateActiveLeaseAsync(
        MutableClock clock,
        StationId stationId)
    {
        var repository = new InMemoryStationAgentControlLeaseRepository(clock);
        var service = new StationAgentControlLeaseService(
            repository,
            new StationAgentControlLeaseOptions
            {
                TimeToLive = TimeSpan.FromMinutes(1)
            });
        var acquired = await service.AcquireAsync(
            stationId,
            AgentA,
            InstanceA,
            LeaseHandleA);
        Assert.True(acquired.IsSuccess);
        Assert.Equal(1, acquired.Value.Observation.Lease!.FencingToken);
        return (repository, service);
    }

    private static string CreateLeaseHandle(byte value) =>
        Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
