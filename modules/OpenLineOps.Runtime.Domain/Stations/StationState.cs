namespace OpenLineOps.Runtime.Domain.Stations;

public enum StationState
{
    Stopped,
    Resetting,
    Idle,
    Starting,
    Execute,
    Completing,
    Complete,
    Holding,
    Held,
    Unholding,
    Suspending,
    Suspended,
    Unsuspending,
    Stopping,
    Aborting,
    Aborted,
    Clearing
}

public enum StationTransitionTrigger
{
    Reset,
    Start,
    Complete,
    Hold,
    Unhold,
    Suspend,
    Unsuspend,
    Stop,
    Abort,
    Clear,
    StateCompleted,
    SafetyPermitLost
}
