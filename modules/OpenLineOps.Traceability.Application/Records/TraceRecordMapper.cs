using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Records;

internal static class TraceRecordMapper
{
    public static TraceRecordDetails ToDetails(TraceRecord record) => new(
        record.Id.Value,
        record.ProductionRunId.Value,
        record.ProductionUnitId.Value,
        record.ProjectId,
        record.ApplicationId,
        record.ProjectSnapshotId,
        record.TopologyId,
        record.ProductionLineDefinitionId,
        record.ProductModelId,
        record.ProductionUnitIdentityInputKey,
        record.ProductionUnitIdentityValue,
        record.LotId,
        record.CarrierId,
        record.ActorId.Value,
        record.ExecutionStatus.ToString(),
        record.Judgement.ToString(),
        record.Disposition.ToString(),
        record.CreatedAtUtc,
        record.StartedAtUtc,
        record.CompletedAtUtc,
        record.FailureCode,
        record.FailureReason,
        record.Operations.Select(ToDetails).ToArray(),
        record.RouteDecisions.Select(ToDetails).ToArray(),
        record.Genealogy.Select(ToDetails).ToArray(),
        record.MaterialLocationTransitions.Select(ToDetails).ToArray(),
        record.SlotOccupancyTransitions.Select(ToDetails).ToArray(),
        record.DispositionTransitions.Select(ToDetails).ToArray(),
        record.AuditEntries.Select(ToDetails).ToArray());

    public static TraceRecordSummary ToSummary(TraceRecord record) => new(
        record.Id.Value,
        record.ProductionRunId.Value,
        record.ProductionUnitId.Value,
        record.ProjectId,
        record.ApplicationId,
        record.ProjectSnapshotId,
        record.TopologyId,
        record.ProductionLineDefinitionId,
        record.ProductModelId,
        record.ProductionUnitIdentityInputKey,
        record.ProductionUnitIdentityValue,
        record.LotId,
        record.CarrierId,
        record.ActorId.Value,
        record.ExecutionStatus.ToString(),
        record.Judgement.ToString(),
        record.Disposition.ToString(),
        record.CompletedAtUtc,
        record.Operations.Count,
        record.Operations.Count(operation => operation.ExecutionStatus is not ExecutionStatus.Completed),
        record.Operations.Sum(operation => operation.Commands.Count),
        record.Operations.Sum(operation => operation.Measurements.Count),
        record.Operations.Sum(operation => operation.Artifacts.Count),
        record.Operations.Sum(operation => operation.Incidents.Count),
        record.RouteDecisions.Count,
        record.Genealogy.Count,
        record.MaterialLocationTransitions.Count,
        record.SlotOccupancyTransitions.Count,
        record.DispositionTransitions.Count);

    private static TraceOperationExecutionDetails ToDetails(TraceOperationExecution operation) => new(
        operation.OperationRunId,
        operation.OperationId,
        operation.Attempt,
        operation.StationSystemId,
        operation.StationId.Value,
        operation.ProcessDefinitionId.Value,
        operation.ProcessVersionId.Value,
        operation.ConfigurationSnapshotId.Value,
        operation.RecipeSnapshotId.Value,
        operation.RuntimeSessionId?.Value,
        operation.RuntimeSessionStatus?.ToString(),
        operation.ExecutionStatus.ToString(),
        operation.Judgement.ToString(),
        operation.StartedAtUtc,
        operation.CompletedAtUtc,
        operation.FailureCode,
        operation.FailureReason,
        operation.CompletedStepCount,
        operation.CommandCount,
        operation.IncidentCount,
        operation.Commands.Select(ToDetails).ToArray(),
        operation.Measurements.Select(ToDetails).ToArray(),
        operation.Artifacts.Select(ToDetails).ToArray(),
        operation.Incidents.Select(ToDetails).ToArray(),
        operation.Outputs.Select(output => new TraceOperationOutputDetails(
            output.Key,
            output.ValueKind,
            output.CanonicalJson)).ToArray(),
        operation.FencingTokens.Select(token => new TraceResourceFencingTokenDetails(
            token.ResourceKind,
            token.ResourceId,
            token.FencingToken)).ToArray());

    private static TraceRouteDecisionDetails ToDetails(TraceRouteDecision decision) => new(
        decision.SourceOperationRunId,
        decision.TransitionId,
        decision.TargetOperationId,
        decision.TerminalDisposition?.ToString(),
        decision.SourceJudgement.ToString(),
        decision.Traversal,
        decision.DecidedAtUtc);

    private static TraceMaterialGenealogyDetails ToDetails(TraceMaterialGenealogy link) => new(
        link.LinkId,
        link.ParentProductionUnitId,
        link.ChildProductionUnitId,
        link.Relationship,
        link.OperationId,
        link.LinkedBy,
        link.LinkedAtUtc);

    private static TraceMaterialLocationTransitionDetails ToDetails(
        TraceMaterialLocationTransition transition) => new(
        transition.EvidenceId,
        transition.ProductionRunId,
        transition.MaterialKind,
        transition.MaterialId,
        transition.Source is null ? null : ToDetails(transition.Source),
        ToDetails(transition.Destination),
        transition.ActorId,
        transition.OccurredAtUtc);

    private static TraceMaterialLocationDetails ToDetails(TraceMaterialLocation location) => new(
        location.Kind,
        location.LineId,
        location.StationSystemId,
        location.SlotId,
        location.CarrierId,
        location.CarrierPositionId);

    private static TraceSlotOccupancyTransitionDetails ToDetails(
        TraceSlotOccupancyTransition transition) => new(
        transition.EvidenceId,
        transition.ProductionRunId,
        transition.LineId,
        transition.StationSystemId,
        transition.SlotId,
        transition.MaterialKind,
        transition.MaterialId,
        transition.PreviousStatus,
        transition.CurrentStatus,
        transition.ActorId,
        transition.OccurredAtUtc);

    private static TraceDispositionTransitionDetails ToDetails(
        TraceDispositionTransition transition) => new(
        transition.EvidenceId,
        transition.ProductionUnitId,
        transition.ProductionRunId,
        transition.PreviousDisposition.ToString(),
        transition.CurrentDisposition.ToString(),
        transition.Reason,
        transition.ActorId,
        transition.OccurredAtUtc);

    private static TraceCommandDetails ToDetails(TraceCommandRecord command) => new(
        command.RuntimeCommandId.Value,
        command.RuntimeStepId,
        command.ActionId,
        command.TargetKind.ToString(),
        command.TargetId,
        command.TargetCapabilityId,
        command.CommandName,
        command.Status.ToString(),
        command.ResultJudgement?.ToString(),
        command.CreatedAtUtc,
        command.DeadlineAtUtc,
        command.AcceptedAtUtc,
        command.StartedAtUtc,
        command.CompletedAtUtc,
        command.ResultPayload,
        command.FailureReason);

    private static MeasurementRecordDetails ToDetails(MeasurementRecord measurement) => new(
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

    private static ArtifactRecordDetails ToDetails(ArtifactRecord artifact) => new(
        artifact.Id.Value,
        artifact.Name,
        artifact.Kind.ToString(),
        artifact.StorageKey,
        artifact.MediaType,
        artifact.SizeBytes,
        artifact.Sha256,
        artifact.DeviceId?.Value,
        artifact.CapturedAtUtc);

    private static TraceIncidentDetails ToDetails(TraceIncidentRecord incident) => new(
        incident.RuntimeIncidentId,
        incident.Severity.ToString(),
        incident.Code,
        incident.Message,
        incident.OccurredAtUtc);

    private static AuditEntryDetails ToDetails(AuditEntry entry) => new(
        entry.Id.Value,
        entry.ActorId.Value,
        entry.Action,
        entry.Detail,
        entry.OccurredAtUtc);
}
