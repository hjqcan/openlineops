namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeTargetStatusResponse(
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    Guid ProductionRunId,
    string ProductionLineDefinitionId,
    string StageId,
    int StageSequence,
    string WorkstationId,
    RuntimeDutIdentityResponse DutIdentity,
    string StationSystemId,
    Guid SessionId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string CommandStatus,
    DateTimeOffset LastTransitionAtUtc,
    bool IsTerminal,
    string? FailureReason);
