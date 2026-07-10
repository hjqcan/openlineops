using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record RuntimeAlarmProjection(
    RuntimeIncidentId AlarmId,
    RuntimeSessionId SessionId,
    string StationSystemId,
    RuntimeIncidentSeverity Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc,
    bool IsAcknowledged,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAtUtc)
{
    public RuntimeAlarmProjection Acknowledge(string acknowledgedBy, DateTimeOffset acknowledgedAtUtc)
    {
        return this with
        {
            IsAcknowledged = true,
            AcknowledgedBy = acknowledgedBy,
            AcknowledgedAtUtc = acknowledgedAtUtc
        };
    }
}
