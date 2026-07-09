namespace OpenLineOps.Runtime.Application.Commands;

public enum RuntimeCommandExecutionOutcome
{
    Completed = 1,
    Failed = 2,
    Rejected = 3,
    TimedOut = 4,
    Canceled = 5
}
