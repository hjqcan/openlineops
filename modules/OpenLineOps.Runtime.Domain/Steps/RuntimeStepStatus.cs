namespace OpenLineOps.Runtime.Domain.Steps;

public enum RuntimeStepStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Skipped = 4,
    Canceled = 5
}
