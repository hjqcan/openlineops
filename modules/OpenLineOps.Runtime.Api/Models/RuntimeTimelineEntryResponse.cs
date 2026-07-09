namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeTimelineEntryResponse(
    long Sequence,
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string EventName,
    Guid SessionId,
    string StationId,
    string EntityKind,
    string? EntityId,
    string? FromStatus,
    string? ToStatus,
    string? Reason,
    string? Severity,
    string? Code,
    string SessionStatus);
