namespace OpenLineOps.Runtime.Domain.Runs;

public enum ProductionRunControlState
{
    Active = 1,
    Paused = 2,
    Held = 3,
    RecoveryRequired = 4,
    SafeStopped = 5
}
