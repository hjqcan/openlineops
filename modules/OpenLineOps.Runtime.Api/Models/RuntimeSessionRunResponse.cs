namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeSessionRunResponse(
    Guid SessionId,
    string ConfigurationSnapshotId,
    string Status,
    int CompletedSteps,
    int CommandCount,
    int IncidentCount);
