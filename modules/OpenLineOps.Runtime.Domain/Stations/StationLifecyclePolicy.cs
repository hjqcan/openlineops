namespace OpenLineOps.Runtime.Domain.Stations;

public sealed record StationTransitionDecision(
    bool Succeeded,
    StationState FromState,
    StationState ToState,
    string Code,
    string Message,
    IReadOnlyList<StationPrerequisite> MissingPrerequisites);

public sealed class StationLifecyclePolicy
{
    private const string AcceptedCode = "Runtime.Accepted";
    private const string InvalidTransitionCode = "Runtime.StationStateTransitionRejected";
    private const string PrerequisitesCode = "Runtime.StationPrerequisitesNotSatisfied";

    private readonly Dictionary<(StationState State, StationTransitionTrigger Trigger), StationState>
        _transitions = BuildTransitions();

    private readonly HashSet<StationState> _safetyPermitLossStableStates =
    [
        StationState.Stopped,
        StationState.Aborting,
        StationState.Aborted
    ];

    public StationTransitionDecision Evaluate(
        StationState currentState,
        StationTransitionTrigger trigger,
        StationReadiness readiness)
    {
        if (!Enum.IsDefined(currentState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentState),
                currentState,
                "Station state is invalid.");
        }

        if (!Enum.IsDefined(trigger))
        {
            throw new ArgumentOutOfRangeException(
                nameof(trigger),
                trigger,
                "Station transition trigger is invalid.");
        }

        ArgumentNullException.ThrowIfNull(readiness);
        if (trigger == StationTransitionTrigger.SafetyPermitLost)
        {
            return EvaluateSafetyPermitLoss(currentState, readiness);
        }

        if (!_transitions.TryGetValue((currentState, trigger), out var targetState))
        {
            return Rejected(
                currentState,
                InvalidTransitionCode,
                $"Trigger {trigger} is not valid while Station is {currentState}.");
        }

        var requirements = RequirementsFor(currentState, trigger);
        var missing = readiness.GetMissing(requirements);
        if (missing.Count > 0)
        {
            return new StationTransitionDecision(
                false,
                currentState,
                currentState,
                PrerequisitesCode,
                $"Station prerequisites are not satisfied: {string.Join(", ", missing)}.",
                missing);
        }

        return new StationTransitionDecision(
            true,
            currentState,
            targetState,
            AcceptedCode,
            "Station state transition accepted.",
            Array.Empty<StationPrerequisite>());
    }

    public bool RequiresAbortOnSafetyPermitLoss(StationState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Station state is invalid.");
        }

        return !_safetyPermitLossStableStates.Contains(state);
    }

    private StationTransitionDecision EvaluateSafetyPermitLoss(
        StationState currentState,
        StationReadiness readiness)
    {
        if (readiness.SafetyPermitGranted)
        {
            return Rejected(
                currentState,
                InvalidTransitionCode,
                "SafetyPermitLost requires a denied safety permit.");
        }

        if (!RequiresAbortOnSafetyPermitLoss(currentState))
        {
            return Rejected(
                currentState,
                InvalidTransitionCode,
                $"Safety permit loss does not change Station state from {currentState}.");
        }

        return new StationTransitionDecision(
            true,
            currentState,
            StationState.Aborting,
            AcceptedCode,
            "Safety permit loss requires the Station to abort.",
            Array.Empty<StationPrerequisite>());
    }

    private static StationReadinessRequirement RequirementsFor(
        StationState state,
        StationTransitionTrigger trigger)
    {
        return trigger switch
        {
            StationTransitionTrigger.Reset => StationReadinessRequirement.SafeToPrepare,
            StationTransitionTrigger.Start
                or StationTransitionTrigger.Unhold
                or StationTransitionTrigger.Unsuspend =>
                StationReadinessRequirement.ReadyToExecute,
            StationTransitionTrigger.Clear =>
                StationReadinessRequirement.InterlocksSatisfied
                | StationReadinessRequirement.SafetyPermitGranted,
            StationTransitionTrigger.StateCompleted when state == StationState.Resetting =>
                StationReadinessRequirement.ResetCompleted,
            StationTransitionTrigger.StateCompleted when state is StationState.Starting
                or StationState.Unholding
                or StationState.Unsuspending =>
                StationReadinessRequirement.ReadyToExecute,
            StationTransitionTrigger.StateCompleted when state == StationState.Clearing =>
                StationReadinessRequirement.InterlocksSatisfied
                | StationReadinessRequirement.SafetyPermitGranted,
            _ => StationReadinessRequirement.None
        };
    }

    private static StationTransitionDecision Rejected(
        StationState currentState,
        string code,
        string message)
    {
        return new StationTransitionDecision(
            false,
            currentState,
            currentState,
            code,
            message,
            Array.Empty<StationPrerequisite>());
    }

    private static Dictionary<(StationState, StationTransitionTrigger), StationState>
        BuildTransitions()
    {
        var transitions =
            new Dictionary<(StationState, StationTransitionTrigger), StationState>
            {
                [(StationState.Stopped, StationTransitionTrigger.Reset)] =
                    StationState.Resetting,
                [(StationState.Complete, StationTransitionTrigger.Reset)] =
                    StationState.Resetting,
                [(StationState.Idle, StationTransitionTrigger.Start)] =
                    StationState.Starting,
                [(StationState.Execute, StationTransitionTrigger.Complete)] =
                    StationState.Completing,
                [(StationState.Execute, StationTransitionTrigger.Hold)] =
                    StationState.Holding,
                [(StationState.Held, StationTransitionTrigger.Unhold)] =
                    StationState.Unholding,
                [(StationState.Execute, StationTransitionTrigger.Suspend)] =
                    StationState.Suspending,
                [(StationState.Suspended, StationTransitionTrigger.Unsuspend)] =
                    StationState.Unsuspending,
                [(StationState.Aborted, StationTransitionTrigger.Clear)] =
                    StationState.Clearing,
                [(StationState.Resetting, StationTransitionTrigger.StateCompleted)] =
                    StationState.Idle,
                [(StationState.Starting, StationTransitionTrigger.StateCompleted)] =
                    StationState.Execute,
                [(StationState.Completing, StationTransitionTrigger.StateCompleted)] =
                    StationState.Complete,
                [(StationState.Holding, StationTransitionTrigger.StateCompleted)] =
                    StationState.Held,
                [(StationState.Unholding, StationTransitionTrigger.StateCompleted)] =
                    StationState.Execute,
                [(StationState.Suspending, StationTransitionTrigger.StateCompleted)] =
                    StationState.Suspended,
                [(StationState.Unsuspending, StationTransitionTrigger.StateCompleted)] =
                    StationState.Execute,
                [(StationState.Stopping, StationTransitionTrigger.StateCompleted)] =
                    StationState.Stopped,
                [(StationState.Aborting, StationTransitionTrigger.StateCompleted)] =
                    StationState.Aborted,
                [(StationState.Clearing, StationTransitionTrigger.StateCompleted)] =
                    StationState.Stopped
            };

        foreach (var state in Enum.GetValues<StationState>())
        {
            if (state is not StationState.Stopped
                and not StationState.Stopping
                and not StationState.Aborting
                and not StationState.Aborted)
            {
                transitions[(state, StationTransitionTrigger.Stop)] = StationState.Stopping;
            }

            if (state is not StationState.Aborting and not StationState.Aborted)
            {
                transitions[(state, StationTransitionTrigger.Abort)] = StationState.Aborting;
            }
        }

        return transitions;
    }
}
