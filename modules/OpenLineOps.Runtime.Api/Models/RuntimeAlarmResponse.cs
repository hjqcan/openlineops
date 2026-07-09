namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeAlarmResponse(
    Guid AlarmId,
    Guid SessionId,
    string StationId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc,
    bool IsAcknowledged,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAtUtc);
