namespace OpenLineOps.Processes.Application.Runtime;

public sealed record StartedProcessRuntimeSessionDetails(
    Guid SessionId,
    string ConfigurationSnapshotId,
    string Status,
    int CompletedSteps,
    int CommandCount,
    int IncidentCount);
