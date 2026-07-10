namespace OpenLineOps.Traceability.Domain.Records;

public enum TraceCommandStatus
{
    Pending,
    Accepted,
    InProgress,
    Completed,
    Failed,
    TimedOut,
    Canceled,
    Rejected
}
