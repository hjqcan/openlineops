namespace OpenLineOps.Processes.Api.Models;

public sealed record StartedProcessRuntimeSessionResponse(
    Guid SessionId,
    string ConfigurationSnapshotId,
    string Status,
    int CompletedSteps,
    int CommandCount,
    int IncidentCount);
