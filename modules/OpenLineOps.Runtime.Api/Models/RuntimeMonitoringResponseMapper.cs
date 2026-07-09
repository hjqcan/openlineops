using OpenLineOps.Runtime.Application.Monitoring;

namespace OpenLineOps.Runtime.Api.Models;

internal static class RuntimeMonitoringResponseMapper
{
    public static RuntimeStationStatusResponse ToResponse(RuntimeStationStatusProjection projection)
    {
        return new RuntimeStationStatusResponse(
            projection.StationId,
            projection.LatestSessionId.Value,
            projection.ProcessDefinitionId,
            projection.ProcessVersionId,
            projection.ConfigurationSnapshotId,
            projection.RecipeSnapshotId,
            projection.SerialNumber,
            projection.BatchId,
            projection.FixtureId,
            projection.DeviceId,
            projection.SessionStatus.ToString(),
            projection.StepCount,
            projection.CompletedStepCount,
            projection.RunningStepCount,
            projection.CommandCount,
            projection.IncidentCount,
            projection.LastTransitionAtUtc,
            projection.IsTerminal);
    }

    public static RuntimeTimelineEntryResponse ToResponse(RuntimeTimelineEntry entry)
    {
        return new RuntimeTimelineEntryResponse(
            entry.Sequence,
            entry.EventId,
            entry.OccurredAtUtc,
            entry.EventName,
            entry.SessionId.Value,
            entry.StationId,
            entry.EntityKind,
            entry.EntityId,
            entry.FromStatus,
            entry.ToStatus,
            entry.Reason,
            entry.Severity,
            entry.Code,
            entry.SessionStatus.ToString());
    }

    public static RuntimeAlarmResponse ToResponse(RuntimeAlarmProjection alarm)
    {
        return new RuntimeAlarmResponse(
            alarm.AlarmId.Value,
            alarm.SessionId.Value,
            alarm.StationId,
            alarm.Severity.ToString(),
            alarm.Code,
            alarm.Message,
            alarm.OccurredAtUtc,
            alarm.IsAcknowledged,
            alarm.AcknowledgedBy,
            alarm.AcknowledgedAtUtc);
    }
}
