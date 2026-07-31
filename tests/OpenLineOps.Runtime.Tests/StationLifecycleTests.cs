using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Tests;

public sealed class StationLifecycleTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private static readonly StationCommandAuthorization FullAuthorization =
        new("operator-a", StationCommandGrant.All);

    [Fact]
    public void CompletePackMlCycleProducesOrderedTransitionAuditAndEvents()
    {
        var station = CreateStation();
        station.ClearDomainEvents();

        AssertAccepted(station.Reset(FullAuthorization, "prepare station", At(1)));
        Assert.Equal(StationState.Resetting, station.State);
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "reset complete",
            At(2)));
        Assert.Equal(StationState.Idle, station.State);
        AssertAccepted(station.Start(FullAuthorization, "start cycle", At(3)));
        Assert.Equal(StationState.Starting, station.State);
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "start complete",
            At(4)));
        Assert.Equal(StationState.Execute, station.State);
        AssertAccepted(station.Complete(FullAuthorization, "cycle complete", At(5)));
        Assert.Equal(StationState.Completing, station.State);
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "completion acknowledged",
            At(6)));

        Assert.Equal(StationState.Complete, station.State);
        Assert.Equal(6, station.TransitionAudit.Count);
        Assert.Equal(
            Enumerable.Range(1, 6).Select(value => (long)value),
            station.TransitionAudit.Select(entry => entry.Sequence));
        Assert.All(
            station.DomainEvents,
            domainEvent => Assert.IsType<StationStateTransitionedDomainEvent>(domainEvent));
    }

    [Fact]
    public void StationStateIncludesTheCompletePackMlStateSet()
    {
        var states = Enum.GetValues<StationState>();

        Assert.Equal(17, states.Length);
        Assert.Contains(StationState.Unholding, states);
        Assert.Contains(StationState.Unsuspending, states);
        Assert.Contains(StationState.Clearing, states);
    }

    [Theory]
    [InlineData(StationPrerequisite.InterlocksSatisfied)]
    [InlineData(StationPrerequisite.Homed)]
    [InlineData(StationPrerequisite.CriticalDevicesHealthy)]
    [InlineData(StationPrerequisite.RecipeVerified)]
    [InlineData(StationPrerequisite.CalibrationValid)]
    public void StartRejectsEveryMissingExecutionPrerequisite(
        StationPrerequisite missingPrerequisite)
    {
        var station = CreateStation();
        MoveToIdle(station);
        AssertAccepted(station.UpdateReadiness(
            ReadyExcept(missingPrerequisite),
            FullAuthorization,
            "readiness changed",
            At(3)));
        station.ClearDomainEvents();

        var result = station.Start(FullAuthorization, "start cycle", At(4));

        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.StationPrerequisitesNotSatisfied", result.Code);
        Assert.Contains(missingPrerequisite.ToString(), result.Message, StringComparison.Ordinal);
        Assert.Equal(StationState.Idle, station.State);
        Assert.Empty(station.DomainEvents);
        Assert.Equal(2, station.TransitionAudit.Count);
    }

    [Fact]
    public void ResetRequiresOnlySafePreparationPrerequisites()
    {
        var readiness = StationReadiness.ReadyToExecute with
        {
            Homed = false,
            RecipeVerified = false,
            CalibrationValid = false
        };
        var station = CreateStation(readiness);

        var result = station.Reset(FullAuthorization, "begin reset", At(1));

        AssertAccepted(result);
        Assert.Equal(StationState.Resetting, station.State);
        var completion = station.AcknowledgeTransition(
            FullAuthorization,
            "reset attempted",
            At(2));
        Assert.False(completion.Succeeded);
        Assert.Contains(
            StationPrerequisite.Homed.ToString(),
            completion.Message,
            StringComparison.Ordinal);
        Assert.Equal(StationState.Resetting, station.State);
    }

    [Fact]
    public void LifecyclePolicyRequiresSafetyPermitBeforeStart()
    {
        var policy = new StationLifecyclePolicy();
        var unsafeReadiness = StationReadiness.ReadyToExecute with
        {
            SafetyPermitGranted = false
        };

        var decision = policy.Evaluate(
            StationState.Idle,
            StationTransitionTrigger.Start,
            unsafeReadiness);

        Assert.False(decision.Succeeded);
        Assert.Equal("Runtime.StationPrerequisitesNotSatisfied", decision.Code);
        Assert.Equal(
            [StationPrerequisite.SafetyPermitGranted],
            decision.MissingPrerequisites);
    }

    [Fact]
    public void UnauthorizedCommandDoesNotChangeStateOrRaiseAnEvent()
    {
        var station = CreateStation();
        MoveToIdle(station);
        station.ClearDomainEvents();
        var readOnlyAuthorization = new StationCommandAuthorization(
            "viewer-a",
            StationCommandGrant.ReportReadiness);

        var result = station.Start(readOnlyAuthorization, "not permitted", At(3));

        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.StationCommandUnauthorized", result.Code);
        Assert.Equal(StationState.Idle, station.State);
        Assert.Empty(station.DomainEvents);
        Assert.Equal(2, station.TransitionAudit.Count);
    }

    [Fact]
    public void ModeCanChangeOnlyWhileStoppedAndWithPermission()
    {
        var station = CreateStation();
        station.ClearDomainEvents();
        var modeAuthorization = new StationCommandAuthorization(
            "engineer-a",
            StationCommandGrant.ChangeMode);

        AssertAccepted(station.ChangeMode(
            StationMode.Maintenance,
            modeAuthorization,
            "maintenance window",
            At(1)));
        Assert.Equal(StationMode.Maintenance, station.Mode);
        var modeEvent = Assert.IsType<StationModeChangedDomainEvent>(
            Assert.Single(station.DomainEvents));
        Assert.Equal(StationMode.Automatic, modeEvent.FromMode);
        Assert.Equal(StationMode.Maintenance, modeEvent.ToMode);

        AssertAccepted(station.Reset(FullAuthorization, "prepare", At(2)));
        var result = station.ChangeMode(
            StationMode.Automatic,
            modeAuthorization,
            "resume production",
            At(3));
        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.StationModeChangeRejected", result.Code);
        Assert.Equal(StationMode.Maintenance, station.Mode);
    }

    [Fact]
    public void HoldAndSuspendPathsRequireExplicitAcknowledgements()
    {
        var station = CreateExecutingStation();

        AssertAccepted(station.Hold(FullAuthorization, "downstream blocked", At(5)));
        Assert.Equal(StationState.Holding, station.State);
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "hold complete",
            At(6)));
        Assert.Equal(StationState.Held, station.State);
        AssertAccepted(station.Unhold(FullAuthorization, "downstream ready", At(7)));
        Assert.Equal(StationState.Unholding, station.State);
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "resume complete",
            At(8)));
        Assert.Equal(StationState.Execute, station.State);

        AssertAccepted(station.Suspend(FullAuthorization, "upstream starved", At(9)));
        Assert.Equal(StationState.Suspending, station.State);
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "suspend complete",
            At(10)));
        Assert.Equal(StationState.Suspended, station.State);
        AssertAccepted(station.Unsuspend(FullAuthorization, "material available", At(11)));
        Assert.Equal(StationState.Unsuspending, station.State);
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "resume complete",
            At(12)));
        Assert.Equal(StationState.Execute, station.State);
    }

    [Fact]
    public void StopAndAbortHaveDistinctRecoveryPaths()
    {
        var stoppedNormally = CreateExecutingStation();
        AssertAccepted(stoppedNormally.Stop(
            FullAuthorization,
            "planned stop",
            At(5)));
        Assert.Equal(StationState.Stopping, stoppedNormally.State);
        AssertAccepted(stoppedNormally.AcknowledgeTransition(
            FullAuthorization,
            "stopped",
            At(6)));
        Assert.Equal(StationState.Stopped, stoppedNormally.State);

        var aborted = CreateExecutingStation();
        AssertAccepted(aborted.Abort(
            FullAuthorization,
            "unrecoverable fault",
            At(5)));
        Assert.Equal(StationState.Aborting, aborted.State);
        AssertAccepted(aborted.AcknowledgeTransition(
            FullAuthorization,
            "energy removed",
            At(6)));
        Assert.Equal(StationState.Aborted, aborted.State);
        AssertAccepted(aborted.Clear(
            FullAuthorization,
            "fault cleared",
            At(7)));
        Assert.Equal(StationState.Clearing, aborted.State);
        AssertAccepted(aborted.AcknowledgeTransition(
            FullAuthorization,
            "clear complete",
            At(8)));
        Assert.Equal(StationState.Stopped, aborted.State);
    }

    [Fact]
    public void SafetyPermitLossAutomaticallyAbortsWithoutAbortPermission()
    {
        var station = CreateExecutingStation();
        station.ClearDomainEvents();
        var safetyReporter = new StationCommandAuthorization(
            "safety-controller-a",
            StationCommandGrant.ReportReadiness);
        var unsafeReadiness = StationReadiness.ReadyToExecute with
        {
            SafetyPermitGranted = false
        };

        var result = station.UpdateReadiness(
            unsafeReadiness,
            safetyReporter,
            "safety gate opened",
            At(5));

        AssertAccepted(result);
        Assert.Equal(StationState.Aborting, station.State);
        Assert.False(station.Readiness.SafetyPermitGranted);
        Assert.Collection(
            station.DomainEvents,
            domainEvent => Assert.IsType<StationReadinessChangedDomainEvent>(domainEvent),
            domainEvent =>
            {
                var transitioned = Assert.IsType<StationStateTransitionedDomainEvent>(
                    domainEvent);
                Assert.Equal(
                    StationTransitionTrigger.SafetyPermitLost,
                    transitioned.Transition.Trigger);
            });
    }

    [Fact]
    public void SafetyPermitLossWhileStoppedBlocksResetWithoutInventingAStateTransition()
    {
        var station = CreateStation();
        station.ClearDomainEvents();
        var unsafeReadiness = StationReadiness.ReadyToExecute with
        {
            SafetyPermitGranted = false
        };

        AssertAccepted(station.UpdateReadiness(
            unsafeReadiness,
            FullAuthorization,
            "safety circuit unavailable",
            At(1)));

        Assert.Equal(StationState.Stopped, station.State);
        Assert.Empty(station.TransitionAudit);
        Assert.Single(station.DomainEvents);
        var reset = station.Reset(FullAuthorization, "reset", At(2));
        Assert.False(reset.Succeeded);
        Assert.Equal("Runtime.StationPrerequisitesNotSatisfied", reset.Code);
    }

    [Fact]
    public void SafetyRecoveryRequiresAbortCompletionClearAndReset()
    {
        var station = CreateExecutingStation();
        var unsafeReadiness = StationReadiness.ReadyToExecute with
        {
            SafetyPermitGranted = false
        };
        AssertAccepted(station.UpdateReadiness(
            unsafeReadiness,
            FullAuthorization,
            "safety permit lost",
            At(5)));
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "abort complete",
            At(6)));
        Assert.Equal(StationState.Aborted, station.State);

        var clearWhileUnsafe = station.Clear(
            FullAuthorization,
            "attempt clear",
            At(7));
        Assert.False(clearWhileUnsafe.Succeeded);
        AssertAccepted(station.UpdateReadiness(
            StationReadiness.ReadyToExecute,
            FullAuthorization,
            "safety restored and verified",
            At(8)));
        AssertAccepted(station.Clear(FullAuthorization, "clear fault", At(9)));
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "clear complete",
            At(10)));
        Assert.Equal(StationState.Stopped, station.State);
        AssertAccepted(station.Reset(FullAuthorization, "prepare", At(11)));
    }

    [Fact]
    public void InvalidTransitionIsRejectedWithoutChangingAudit()
    {
        var station = CreateStation();
        station.ClearDomainEvents();

        var result = station.Start(FullAuthorization, "invalid start", At(1));

        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.StationStateTransitionRejected", result.Code);
        Assert.Equal(StationState.Stopped, station.State);
        Assert.Empty(station.TransitionAudit);
        Assert.Empty(station.DomainEvents);
    }

    [Fact]
    public void SnapshotRoundTripPreservesStateReadinessAndTransitionAudit()
    {
        var station = CreateExecutingStation();
        AssertAccepted(station.Hold(FullAuthorization, "pause", At(5)));
        var snapshot = station.ToSnapshot();

        var restored = StationLifecycle.Restore(snapshot);

        Assert.Equal(station.Id, restored.Id);
        Assert.Equal(station.Mode, restored.Mode);
        Assert.Equal(StationState.Holding, restored.State);
        Assert.Equal(station.Readiness, restored.Readiness);
        Assert.Equal(station.LastChangedAtUtc, restored.LastChangedAtUtc);
        Assert.Equal(station.TransitionAudit, restored.TransitionAudit);
        Assert.Empty(restored.DomainEvents);
    }

    [Fact]
    public void RestoreRejectsDiscontinuousTransitionAudit()
    {
        var snapshot = CreateExecutingStation().ToSnapshot();
        var corrupted = snapshot with
        {
            TransitionAudit = snapshot.TransitionAudit
                .Select((entry, index) => index == 1
                    ? entry with { FromState = StationState.Complete }
                    : entry)
                .ToArray()
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => StationLifecycle.Restore(corrupted));

        Assert.Contains("continuous", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreRejectsActiveStateWithDeniedSafetyPermit()
    {
        var snapshot = CreateExecutingStation().ToSnapshot();
        var corrupted = snapshot with
        {
            Readiness = snapshot.Readiness with { SafetyPermitGranted = false }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => StationLifecycle.Restore(corrupted));

        Assert.Contains("safety permit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LifecycleMutationsRequireMonotonicUtcTimestamps()
    {
        var station = CreateStation();

        Assert.Throws<ArgumentOutOfRangeException>(() => station.Reset(
            FullAuthorization,
            "old command",
            BaseTimeUtc.AddSeconds(-1)));
        Assert.Throws<ArgumentException>(() => station.Reset(
            FullAuthorization,
            "local timestamp",
            new DateTimeOffset(2026, 7, 31, 8, 0, 1, TimeSpan.FromHours(8))));
    }

    private static StationLifecycle CreateStation(
        StationReadiness? readiness = null)
    {
        return StationLifecycle.Create(
            new StationId("station-a"),
            StationMode.Automatic,
            readiness ?? StationReadiness.ReadyToExecute,
            "engineer-a",
            BaseTimeUtc);
    }

    private static StationLifecycle CreateExecutingStation()
    {
        var station = CreateStation();
        MoveToIdle(station);
        AssertAccepted(station.Start(FullAuthorization, "start", At(3)));
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "started",
            At(4)));
        Assert.Equal(StationState.Execute, station.State);
        return station;
    }

    private static void MoveToIdle(StationLifecycle station)
    {
        AssertAccepted(station.Reset(FullAuthorization, "reset", At(1)));
        AssertAccepted(station.AcknowledgeTransition(
            FullAuthorization,
            "reset complete",
            At(2)));
        Assert.Equal(StationState.Idle, station.State);
    }

    private static StationReadiness ReadyExcept(StationPrerequisite prerequisite)
    {
        return prerequisite switch
        {
            StationPrerequisite.InterlocksSatisfied =>
                StationReadiness.ReadyToExecute with { InterlocksSatisfied = false },
            StationPrerequisite.Homed =>
                StationReadiness.ReadyToExecute with { Homed = false },
            StationPrerequisite.CriticalDevicesHealthy =>
                StationReadiness.ReadyToExecute with { CriticalDevicesHealthy = false },
            StationPrerequisite.RecipeVerified =>
                StationReadiness.ReadyToExecute with { RecipeVerified = false },
            StationPrerequisite.CalibrationValid =>
                StationReadiness.ReadyToExecute with { CalibrationValid = false },
            StationPrerequisite.SafetyPermitGranted =>
                StationReadiness.ReadyToExecute with { SafetyPermitGranted = false },
            _ => throw new ArgumentOutOfRangeException(nameof(prerequisite), prerequisite, null)
        };
    }

    private static DateTimeOffset At(int seconds)
    {
        return BaseTimeUtc.AddSeconds(seconds);
    }

    private static void AssertAccepted(
        OpenLineOps.Runtime.Domain.Operations.RuntimeOperationResult result)
    {
        Assert.True(result.Succeeded, result.Message);
    }
}
