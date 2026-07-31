using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Api.Models;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Application.Records;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Traceability)]
[Route(OpenLineOpsApiRoutes.Traceability + "/records")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class TraceRecordsController : ControllerBase
{
    private readonly ITraceRecordService _traceRecordService;

    public TraceRecordsController(ITraceRecordService traceRecordService)
    {
        _traceRecordService = traceRecordService;
    }

    [HttpGet]
    [ProducesResponseType<PagedTraceRecordSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedTraceRecordSummaryResponse>> QueryAsync(
        [FromQuery] Guid? productionRunId,
        [FromQuery] Guid? productionUnitId,
        [FromQuery] string? productModelId,
        [FromQuery] string? productionUnitIdentityInputKey,
        [FromQuery] string? productionUnitIdentityValue,
        [FromQuery] string? lotId,
        [FromQuery] string? carrierId,
        [FromQuery] string? actorId,
        [FromQuery] string? executionStatus,
        [FromQuery] string? judgement,
        [FromQuery] string? disposition,
        [FromQuery] string? projectId,
        [FromQuery] string? applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? topologyId,
        [FromQuery] string? productionLineDefinitionId,
        [FromQuery] string? operationId,
        [FromQuery] string? stationSystemId,
        [FromQuery] string? stationId,
        [FromQuery] string? processDefinitionId,
        [FromQuery] string? processVersionId,
        [FromQuery] string? configurationSnapshotId,
        [FromQuery] string? recipeSnapshotId,
        [FromQuery] string? resourceKind,
        [FromQuery] string? resourceId,
        [FromQuery] string? deviceId,
        [FromQuery] DateTimeOffset? completedFromUtc,
        [FromQuery] DateTimeOffset? completedToUtc,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _traceRecordService.QueryAsync(
            new TraceRecordQuery(
                productionRunId,
                productionUnitId,
                productModelId,
                productionUnitIdentityInputKey,
                productionUnitIdentityValue,
                lotId,
                carrierId,
                actorId,
                executionStatus,
                judgement,
                disposition,
                projectId,
                applicationId,
                projectSnapshotId,
                topologyId,
                productionLineDefinitionId,
                operationId,
                stationSystemId,
                stationId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                recipeSnapshotId,
                resourceKind,
                resourceId,
                deviceId,
                completedFromUtc,
                completedToUtc,
                new PagedRequest(pageNumber, pageSize)),
            cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(new PagedTraceRecordSummaryResponse(
                result.Value.Items.Select(ToSummaryResponse).ToArray(),
                result.Value.PageNumber,
                result.Value.PageSize,
                result.Value.TotalCount,
                result.Value.TotalPages));
    }

    [HttpGet("{traceRecordId:guid}")]
    [ProducesResponseType<TraceRecordResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TraceRecordResponse>> GetByIdAsync(
        Guid traceRecordId,
        CancellationToken cancellationToken)
    {
        var result = await _traceRecordService
            .GetByIdAsync(traceRecordId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpGet("{traceRecordId:guid}/export")]
    [ProducesResponseType<TraceRecordExportPackageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TraceRecordExportPackageResponse>> ExportAsync(
        Guid traceRecordId,
        CancellationToken cancellationToken)
    {
        var result = await _traceRecordService
            .ExportAsync(traceRecordId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(new TraceRecordExportPackageResponse(
                result.Value.PackageFormat,
                result.Value.ExportedAtUtc,
                ToResponse(result.Value.TraceRecord)));
    }

    private static TraceRecordResponse ToResponse(TraceRecordDetails details) =>
        new(
            details.TraceRecordId,
            details.ProductionRunId,
            details.ProductionUnitId,
            details.ProjectId,
            details.ApplicationId,
            details.ProjectSnapshotId,
            details.TopologyId,
            details.ProductionLineDefinitionId,
            details.ProductModelId,
            details.ProductionUnitIdentityInputKey,
            details.ProductionUnitIdentityValue,
            details.LotId,
            details.CarrierId,
            details.ActorId,
            details.ExecutionStatus,
            details.Judgement,
            details.Disposition,
            details.CreatedAtUtc,
            details.StartedAtUtc,
            details.CompletedAtUtc,
            details.FailureCode,
            details.FailureReason,
            details.Operations.Select(ToResponse).ToArray(),
            details.RouteDecisions.Select(ToResponse).ToArray(),
            details.Genealogy.Select(ToResponse).ToArray(),
            details.MaterialLocationTransitions.Select(ToResponse).ToArray(),
            details.SlotOccupancyTransitions.Select(ToResponse).ToArray(),
            details.DispositionTransitions.Select(ToResponse).ToArray(),
            details.AuditEntries.Select(ToResponse).ToArray());

    private static TraceRecordSummaryResponse ToSummaryResponse(TraceRecordSummary summary) =>
        new(
            summary.TraceRecordId,
            summary.ProductionRunId,
            summary.ProductionUnitId,
            summary.ProjectId,
            summary.ApplicationId,
            summary.ProjectSnapshotId,
            summary.TopologyId,
            summary.ProductionLineDefinitionId,
            summary.ProductModelId,
            summary.ProductionUnitIdentityInputKey,
            summary.ProductionUnitIdentityValue,
            summary.LotId,
            summary.CarrierId,
            summary.ActorId,
            summary.ExecutionStatus,
            summary.Judgement,
            summary.Disposition,
            summary.CompletedAtUtc,
            summary.OperationCount,
            summary.FailedOperationCount,
            summary.CommandCount,
            summary.MeasurementCount,
            summary.ArtifactCount,
            summary.IncidentCount,
            summary.RouteDecisionCount,
            summary.GenealogyCount,
            summary.MaterialLocationTransitionCount,
            summary.SlotOccupancyTransitionCount,
            summary.DispositionTransitionCount);

    private static TraceOperationExecutionResponse ToResponse(TraceOperationExecutionDetails operation) =>
        new(
            operation.OperationRunId,
            operation.OperationId,
            operation.Attempt,
            operation.StationSystemId,
            operation.StationId,
            operation.ProcessDefinitionId,
            operation.ProcessVersionId,
            operation.ConfigurationSnapshotId,
            operation.RecipeSnapshotId,
            operation.RuntimeSessionId,
            operation.RuntimeSessionStatus,
            operation.ExecutionStatus,
            operation.Judgement,
            operation.StartedAtUtc,
            operation.CompletedAtUtc,
            operation.FailureCode,
            operation.FailureReason,
            operation.CompletedStepCount,
            operation.CommandCount,
            operation.IncidentCount,
            operation.Commands.Select(ToResponse).ToArray(),
            operation.Measurements.Select(ToResponse).ToArray(),
            operation.Artifacts.Select(ToResponse).ToArray(),
            operation.Incidents.Select(ToResponse).ToArray(),
            operation.Outputs.Select(ToResponse).ToArray(),
            operation.FencingTokens.Select(ToResponse).ToArray());

    private static TraceRouteDecisionResponse ToResponse(TraceRouteDecisionDetails decision) =>
        new(
            decision.SourceOperationRunId,
            decision.TransitionId,
            decision.TargetOperationId,
            decision.TerminalDisposition,
            decision.SourceJudgement,
            decision.Traversal,
            decision.DecidedAtUtc);

    private static TraceMaterialGenealogyResponse ToResponse(TraceMaterialGenealogyDetails link) =>
        new(
            link.LinkId,
            link.ParentProductionUnitId,
            link.ChildProductionUnitId,
            link.Relationship,
            link.OperationId,
            link.LinkedBy,
            link.LinkedAtUtc);

    private static TraceMaterialLocationTransitionResponse ToResponse(
        TraceMaterialLocationTransitionDetails transition) => new(
        transition.EvidenceId,
        transition.ProductionRunId,
        transition.MaterialKind,
        transition.MaterialId,
        transition.Source is null ? null : ToResponse(transition.Source),
        ToResponse(transition.Destination),
        transition.ActorId,
        transition.OccurredAtUtc);

    private static TraceMaterialLocationResponse ToResponse(TraceMaterialLocationDetails location) =>
        new(
            location.Kind,
            location.LineId,
            location.StationSystemId,
            location.SlotId,
            location.CarrierId,
            location.CarrierPositionId);

    private static TraceSlotOccupancyTransitionResponse ToResponse(
        TraceSlotOccupancyTransitionDetails transition) => new(
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

    private static TraceDispositionTransitionResponse ToResponse(
        TraceDispositionTransitionDetails transition) => new(
        transition.EvidenceId,
        transition.ProductionUnitId,
        transition.ProductionRunId,
        transition.PreviousDisposition,
        transition.CurrentDisposition,
        transition.Reason,
        transition.ActorId,
        transition.OccurredAtUtc);

    private static TraceOperationOutputResponse ToResponse(TraceOperationOutputDetails output) =>
        new(output.Key, output.ValueKind, output.CanonicalJson);

    private static TraceResourceFencingTokenResponse ToResponse(TraceResourceFencingTokenDetails token) =>
        new(token.ResourceKind, token.ResourceId, token.FencingToken);

    private static TraceCommandResponse ToResponse(TraceCommandDetails command) =>
        new(
            command.RuntimeCommandId,
            command.RuntimeStepId,
            command.ActionId,
            command.TargetKind,
            command.TargetId,
            command.TargetCapabilityId,
            command.CommandName,
            command.ExecutionStatus,
            command.ResultJudgement,
            command.CreatedAtUtc,
            command.DeadlineAtUtc,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);

    private static MeasurementRecordResponse ToResponse(MeasurementRecordDetails measurement) =>
        new(
            measurement.MeasurementRecordId,
            measurement.Name,
            measurement.NumericValue,
            measurement.TextValue,
            measurement.Unit,
            measurement.DeviceId,
            measurement.RuntimeCommandId,
            measurement.ActionId,
            measurement.TargetKind,
            measurement.TargetId,
            measurement.CommandExecutionStatus,
            measurement.CommandResultJudgement,
            measurement.Passed,
            measurement.MeasuredAtUtc);

    private static ArtifactRecordResponse ToResponse(ArtifactRecordDetails artifact) =>
        new(
            artifact.ArtifactRecordId,
            artifact.Name,
            artifact.Kind,
            artifact.StorageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256,
            artifact.DeviceId,
            artifact.CapturedAtUtc);

    private static TraceIncidentResponse ToResponse(TraceIncidentDetails incident) =>
        new(incident.RuntimeIncidentId, incident.Severity, incident.Code, incident.Message, incident.OccurredAtUtc);

    private static AuditEntryResponse ToResponse(AuditEntryDetails auditEntry) =>
        new(
            auditEntry.AuditEntryId,
            auditEntry.ActorId,
            auditEntry.Action,
            auditEntry.Detail,
            auditEntry.OccurredAtUtc);

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };
        return Problem(title: error.Code, detail: error.Message, statusCode: statusCode);
    }
}
