using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Sessions;

public sealed record RuntimeSessionRunResult(
    RuntimeSessionId SessionId,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    RuntimeSessionStatus Status,
    int CompletedSteps,
    int CommandCount,
    int IncidentCount);
