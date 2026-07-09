namespace OpenLineOps.Runtime.Contracts;

public enum RuntimeCommandStatus
{
    Accepted = 1,
    Rejected = 2,
    InProgress = 3,
    Completed = 4,
    Failed = 5,
    TimedOut = 6
}
