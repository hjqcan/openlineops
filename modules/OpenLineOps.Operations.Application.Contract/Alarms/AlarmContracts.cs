using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Application.Contract.Alarms;

public sealed record AlarmDetails(
    string Id,
    string StationId,
    string Source,
    string? SourceId,
    AlarmSeverity Severity,
    AlarmStatus Status,
    string Title,
    string Description,
    DateTimeOffset RaisedAtUtc,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAtUtc,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAtUtc,
    string? ResolutionNote);

public sealed record RaiseAlarmRequest(
    string Id,
    string StationId,
    string Source,
    string? SourceId,
    AlarmSeverity Severity,
    string Title,
    string Description,
    DateTimeOffset? RaisedAtUtc = null);

public sealed record AcknowledgeAlarmRequest(string AcknowledgedBy);

public sealed record ResolveAlarmRequest(
    string ResolvedBy,
    string ResolutionNote);
