using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Monitoring;

public interface IRuntimeMonitoringStore
{
    ValueTask RebuildAsync(CancellationToken cancellationToken = default);

    ValueTask ApplyPendingAsync(
        IReadOnlyCollection<Guid> requiredEventIds,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeStationStatusProjection>> ListStationStatusesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeTargetStatusProjection>> ListTargetStatusesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeTimelineEntry>> ListTimelineAsync(
        RuntimeSessionId sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeAlarmProjection>> ListAlarmsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<RuntimeAlarmProjection?> AcknowledgeAlarmAsync(
        RuntimeIncidentId alarmId,
        string acknowledgedBy,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default);
}
