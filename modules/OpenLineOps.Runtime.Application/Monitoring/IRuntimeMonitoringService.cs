using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Monitoring;

public interface IRuntimeMonitoringService
{
    ValueTask<IReadOnlyCollection<RuntimeStationStatusProjection>> GetStationStatusesAsync(
        RuntimeMonitoringScope scope,
        string? stationSystemId = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeTargetStatusProjection>> GetTargetStatusesAsync(
        RuntimeMonitoringScope scope,
        string? stationSystemId = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeTimelineEntry>> GetSessionTimelineAsync(
        RuntimeSessionId sessionId,
        RuntimeMonitoringScope scope,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeAlarmProjection>> GetAlarmsAsync(
        string? stationSystemId = null,
        bool includeAcknowledged = false,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RuntimeAlarmProjection>> AcknowledgeAlarmAsync(
        RuntimeIncidentId alarmId,
        string acknowledgedBy,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default);
}
