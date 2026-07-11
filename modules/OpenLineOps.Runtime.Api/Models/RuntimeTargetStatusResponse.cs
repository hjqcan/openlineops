namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeTargetStatusResponse(
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    Guid ProductionRunId,
    string ProductionLineDefinitionId,
    string OperationId,
    int OperationAttempt,
    string StationSystemId,
    RuntimeProductionUnitIdentityResponse ProductionUnitIdentity,
    string RuntimeStationId,
    Guid SessionId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string CommandStatus,
    DateTimeOffset LastTransitionAtUtc,
    bool IsTerminal,
    string? FailureReason);
