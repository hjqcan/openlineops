namespace OpenLineOps.Runtime.Contracts;

public enum RuntimeSessionState
{
    Created = 0,
    Queued = 1,
    Running = 2,
    Pausing = 3,
    Paused = 4,
    Stopping = 5,
    Stopped = 6,
    Completed = 7,
    Failed = 8,
    Canceled = 9
}
