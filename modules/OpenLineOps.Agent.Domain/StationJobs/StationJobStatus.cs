namespace OpenLineOps.Agent.Domain.StationJobs;

public enum StationJobStatus
{
    Requested = 0,
    Accepted = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    TimedOut = 5,
    Canceled = 6,
    Rejected = 7,
    RecoveryRequired = 8
}
