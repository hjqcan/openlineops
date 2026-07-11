using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record RuntimeTimelineEntry(
    long Sequence,
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string EventName,
    RuntimeSessionId SessionId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    ProductionRunId ProductionRunId,
    string ProductionLineDefinitionId,
    string OperationId,
    int OperationAttempt,
    string StationSystemId,
    ProductionUnitIdentity ProductionUnitIdentity,
    string RuntimeStationId,
    string EntityKind,
    string? EntityId,
    string? FromStatus,
    string? ToStatus,
    string? Reason,
    string? Severity,
    string? Code,
    RuntimeSessionStatus SessionStatus);
