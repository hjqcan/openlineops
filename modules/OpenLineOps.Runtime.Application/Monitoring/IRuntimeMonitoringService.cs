using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Monitoring;

public interface IRuntimeMonitoringService
{
    ValueTask<IReadOnlyCollection<RuntimeStationStatusProjection>> GetStationStatusesAsync(
        string? stationId = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeTimelineEntry>> GetSessionTimelineAsync(
        RuntimeSessionId sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeAlarmProjection>> GetAlarmsAsync(
        string? stationId = null,
        bool includeAcknowledged = false,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RuntimeAlarmProjection>> AcknowledgeAlarmAsync(
        RuntimeIncidentId alarmId,
        string acknowledgedBy,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default);
}
