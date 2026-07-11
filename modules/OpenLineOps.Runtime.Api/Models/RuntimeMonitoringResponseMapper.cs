using OpenLineOps.Runtime.Application.Monitoring;

namespace OpenLineOps.Runtime.Api.Models;

internal static class RuntimeMonitoringResponseMapper
{
    public static RuntimeStationStatusResponse ToResponse(RuntimeStationStatusProjection projection)
    {
        return new RuntimeStationStatusResponse(
            projection.ProjectId,
            projection.ApplicationId,
            projection.ProjectSnapshotId,
            projection.TopologyId,
            projection.ProductionRunId.Value,
            projection.ProductionLineDefinitionId,
            projection.OperationId,
            projection.OperationAttempt,
            projection.StationSystemId,
            new RuntimeProductionUnitIdentityResponse(
                projection.ProductionUnitIdentity.ModelId,
                projection.ProductionUnitIdentity.InputKey,
                projection.ProductionUnitIdentity.Value),
            projection.RuntimeStationId,
            projection.LatestSessionId.Value,
            projection.ProcessDefinitionId,
            projection.ProcessVersionId,
            projection.ConfigurationSnapshotId,
            projection.RecipeSnapshotId,
            projection.LotId,
            projection.CarrierId,
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

    public static RuntimeTargetStatusResponse ToResponse(RuntimeTargetStatusProjection projection)
    {
        return new RuntimeTargetStatusResponse(
            projection.ProjectId,
            projection.ApplicationId,
            projection.ProjectSnapshotId,
            projection.TopologyId,
            projection.ProductionRunId.Value,
            projection.ProductionLineDefinitionId,
            projection.OperationId,
            projection.OperationAttempt,
            projection.StationSystemId,
            new RuntimeProductionUnitIdentityResponse(
                projection.ProductionUnitIdentity.ModelId,
                projection.ProductionUnitIdentity.InputKey,
                projection.ProductionUnitIdentity.Value),
            projection.RuntimeStationId,
            projection.SessionId.Value,
            projection.ActionId,
            projection.TargetKind,
            projection.TargetId,
            projection.CommandStatus.ToString(),
            projection.LastTransitionAtUtc,
            projection.IsTerminal,
            projection.FailureReason);
    }

    public static RuntimeTimelineEntryResponse ToResponse(RuntimeTimelineEntry entry)
    {
        return new RuntimeTimelineEntryResponse(
            entry.Sequence,
            entry.EventId,
            entry.OccurredAtUtc,
            entry.EventName,
            entry.SessionId.Value,
            entry.ProjectId,
            entry.ApplicationId,
            entry.ProjectSnapshotId,
            entry.TopologyId,
            entry.ProductionRunId.Value,
            entry.ProductionLineDefinitionId,
            entry.OperationId,
            entry.OperationAttempt,
            entry.StationSystemId,
            new RuntimeProductionUnitIdentityResponse(
                entry.ProductionUnitIdentity.ModelId,
                entry.ProductionUnitIdentity.InputKey,
                entry.ProductionUnitIdentity.Value),
            entry.RuntimeStationId,
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
            alarm.StationSystemId,
            alarm.Severity.ToString(),
            alarm.Code,
            alarm.Message,
            alarm.OccurredAtUtc,
            alarm.IsAcknowledged,
            alarm.AcknowledgedBy,
            alarm.AcknowledgedAtUtc);
    }
}
