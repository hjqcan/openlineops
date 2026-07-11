namespace OpenLineOps.Traceability.Domain.Records;

public enum TraceRuntimeSessionStatus
{
    Created,
    Queued,
    Running,
    Pausing,
    Paused,
    Stopping,
    Stopped,
    Completed,
    Failed,
    Canceled,
    Reconciled
}
