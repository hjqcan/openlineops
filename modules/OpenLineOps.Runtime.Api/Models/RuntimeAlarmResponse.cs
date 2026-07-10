namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeAlarmResponse(
    Guid AlarmId,
    Guid SessionId,
    string StationSystemId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc,
    bool IsAcknowledged,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAtUtc);
