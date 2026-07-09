using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Domain.Shared.IntegrationEvents;

public sealed record AlarmRaisedIntegrationDto(
    string AlarmId,
    string StationId,
    string Source,
    string? SourceId,
    AlarmSeverity Severity,
    string Title,
    string Description,
    DateTimeOffset RaisedAtUtc)
{
    public const string EventName = "Operations.Alarm.Raised";

    public const string Version = "v1";
}
