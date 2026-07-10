using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record RuntimeTimelineEntry(
    long Sequence,
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string EventName,
    RuntimeSessionId SessionId,
    string StationSystemId,
    string EntityKind,
    string? EntityId,
    string? FromStatus,
    string? ToStatus,
    string? Reason,
    string? Severity,
    string? Code,
    RuntimeSessionStatus SessionStatus);
