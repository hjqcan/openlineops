namespace OpenLineOps.Runtime.Domain.Commands;

public enum RuntimeCommandStatus
{
    Pending = 0,
    Accepted = 1,
    InProgress = 2,
    Completed = 3,
    Failed = 4,
    TimedOut = 5,
    Canceled = 6,
    Rejected = 7
}
