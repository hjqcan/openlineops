using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Records;

internal static class TraceRecordMapper
{
    public static TraceRecordDetails ToDetails(TraceRecord record)
    {
        return new TraceRecordDetails(
            record.Id.Value,
            record.ProductionRunId.Value,
            record.ProjectId,
            record.ApplicationId,
            record.ProjectSnapshotId,
            record.TopologyId,
            record.ProductionLineDefinitionId,
            record.DutModelId,
            record.DutIdentityInputKey,
            record.DutIdentityValue,
            record.BatchId,
            record.FixtureId,
            record.DeviceId,
            record.ActorId.Value,
            record.RunStatus.ToString(),
            record.Judgement.ToString(),
            record.CreatedAtUtc,
            record.StartedAtUtc,
            record.CompletedAtUtc,
            record.FailureCode,
            record.FailureReason,
            record.Stages.Select(ToDetails).ToArray(),
            record.AuditEntries.Select(ToDetails).ToArray());
    }

    public static TraceRecordSummary ToSummary(TraceRecord record)
    {
        return new TraceRecordSummary(
            record.Id.Value,
            record.ProductionRunId.Value,
            record.ProjectId,
            record.ApplicationId,
            record.ProjectSnapshotId,
            record.TopologyId,
            record.ProductionLineDefinitionId,
            record.DutModelId,
            record.DutIdentityInputKey,
            record.DutIdentityValue,
            record.BatchId,
            record.FixtureId,
            record.DeviceId,
            record.ActorId.Value,
            record.RunStatus.ToString(),
            record.Judgement.ToString(),
            record.CompletedAtUtc,
            record.Stages.Count,
            record.Stages.Count(stage => stage.Status is TraceStageStatus.Failed or TraceStageStatus.Canceled),
            record.Stages.Sum(stage => stage.Commands.Count),
            record.Stages.Sum(stage => stage.Measurements.Count),
            record.Stages.Sum(stage => stage.Artifacts.Count),
            record.Stages.Sum(stage => stage.Incidents.Count));
    }

    private static TraceStageExecutionDetails ToDetails(TraceStageExecution stage)
    {
        return new TraceStageExecutionDetails(
            stage.StageId,
            stage.Sequence,
            stage.WorkstationId,
            stage.StationId.Value,
            stage.ProcessDefinitionId.Value,
            stage.ProcessVersionId.Value,
            stage.ConfigurationSnapshotId.Value,
            stage.RecipeSnapshotId.Value,
            stage.RuntimeSessionId?.Value,
            stage.RuntimeSessionStatus?.ToString(),
            stage.Status.ToString(),
            stage.StartedAtUtc,
            stage.CompletedAtUtc,
            stage.FailureCode,
            stage.FailureReason,
            stage.CompletedStepCount,
            stage.CommandCount,
            stage.IncidentCount,
            stage.Commands.Select(ToDetails).ToArray(),
            stage.Measurements.Select(ToDetails).ToArray(),
            stage.Artifacts.Select(ToDetails).ToArray(),
            stage.Incidents.Select(ToDetails).ToArray());
    }

    private static TraceCommandDetails ToDetails(TraceCommandRecord command)
    {
        return new TraceCommandDetails(
            command.RuntimeCommandId.Value,
            command.RuntimeStepId,
            command.ActionId,
            command.TargetKind.ToString(),
            command.TargetId,
            command.TargetCapabilityId,
            command.CommandName,
            command.Status.ToString(),
            command.SemanticOutcome?.ToString(),
            command.CreatedAtUtc,
            command.DeadlineAtUtc,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static MeasurementRecordDetails ToDetails(MeasurementRecord measurement)
    {
        return new MeasurementRecordDetails(
            measurement.Id.Value,
            measurement.Name,
            measurement.NumericValue,
            measurement.TextValue,
            measurement.Unit,
            measurement.DeviceId?.Value,
            measurement.RuntimeCommandId?.Value,
            measurement.ActionId,
            measurement.TargetKind.ToString(),
            measurement.TargetId,
            measurement.CommandStatus.ToString(),
            measurement.Passed,
            measurement.MeasuredAtUtc);
    }

    private static ArtifactRecordDetails ToDetails(ArtifactRecord artifact)
    {
        return new ArtifactRecordDetails(
            artifact.Id.Value,
            artifact.Name,
            artifact.Kind.ToString(),
            artifact.StorageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256,
            artifact.DeviceId?.Value,
            artifact.CapturedAtUtc);
    }

    private static TraceIncidentDetails ToDetails(TraceIncidentRecord incident)
    {
        return new TraceIncidentDetails(
            incident.RuntimeIncidentId,
            incident.Severity.ToString(),
            incident.Code,
            incident.Message,
            incident.OccurredAtUtc);
    }

    private static AuditEntryDetails ToDetails(AuditEntry entry)
    {
        return new AuditEntryDetails(
            entry.Id.Value,
            entry.ActorId.Value,
            entry.Action,
            entry.Detail,
            entry.OccurredAtUtc);
    }
}
