namespace OpenLineOps.Runtime.Domain.Runs;

public enum ProductionStageRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
    Skipped
}
