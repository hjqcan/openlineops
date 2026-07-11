using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Api.Models;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Application.Records;
using ApiCreateRequest = OpenLineOps.Traceability.Api.Models.CreateTraceRecordRequest;
using AppCreateRequest = OpenLineOps.Traceability.Application.Records.CreateTraceRecordRequest;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Traceability)]
[Route(OpenLineOpsApiRoutes.Traceability + "/records")]
public sealed class TraceRecordsController : ControllerBase
{
    private readonly ITraceRecordService _traceRecordService;

    public TraceRecordsController(ITraceRecordService traceRecordService)
    {
        _traceRecordService = traceRecordService;
    }

    [HttpPost]
    [ProducesResponseType<TraceRecordResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TraceRecordResponse>> CreateAsync(
        ApiCreateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _traceRecordService
            .CreateAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);
        return Created($"/api/traceability/records/{response.TraceRecordId}", response);
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

    private static AppCreateRequest ToApplicationRequest(ApiCreateRequest request)
    {
        return new AppCreateRequest(
            request.ProductionRunId,
            request.ProductionUnitId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId,
            request.ProductionLineDefinitionId,
            request.ProductModelId,
            request.ProductionUnitIdentityInputKey,
            request.ProductionUnitIdentityValue,
            request.LotId,
            request.CarrierId,
            request.ActorId,
            request.ExecutionStatus,
            request.Judgement,
            request.Disposition,
            request.CreatedAtUtc,
            request.StartedAtUtc,
            request.CompletedAtUtc,
            request.FailureCode,
            request.FailureReason,
            request.Operations?.Select(ToApplicationRequest).ToArray(),
            request.RouteDecisions?.Select(ToApplicationRequest).ToArray(),
            request.Genealogy?.Select(ToApplicationRequest).ToArray(),
            request.MaterialLocationTransitions?.Select(ToApplicationRequest).ToArray(),
            request.SlotOccupancyTransitions?.Select(ToApplicationRequest).ToArray(),
            request.DispositionTransitions?.Select(ToApplicationRequest).ToArray(),
            request.AuditEntries?.Select(ToApplicationRequest).ToArray());
    }

    private static Application.Records.CreateTraceOperationExecutionRequest ToApplicationRequest(
        Models.CreateTraceOperationExecutionRequest request)
    {
        return new Application.Records.CreateTraceOperationExecutionRequest(
            request.OperationRunId,
            request.OperationId,
            request.Attempt,
            request.StationSystemId,
            request.StationId,
            request.ProcessDefinitionId,
            request.ProcessVersionId,
            request.ConfigurationSnapshotId,
            request.RecipeSnapshotId,
            request.RuntimeSessionId,
            request.RuntimeSessionStatus,
            request.ExecutionStatus,
            request.Judgement,
            request.StartedAtUtc,
            request.CompletedAtUtc,
            request.FailureCode,
            request.FailureReason,
            request.CompletedStepCount,
            request.CommandCount,
            request.IncidentCount,
            request.Commands?.Select(ToApplicationRequest).ToArray(),
            request.Measurements?.Select(ToApplicationRequest).ToArray(),
            request.Artifacts?.Select(ToApplicationRequest).ToArray(),
            request.Incidents?.Select(ToApplicationRequest).ToArray(),
            request.Outputs?.Select(ToApplicationRequest).ToArray(),
            request.FencingTokens?.Select(ToApplicationRequest).ToArray());
    }

    private static Application.Records.CreateTraceRouteDecisionRequest ToApplicationRequest(
        Models.CreateTraceRouteDecisionRequest request) =>
        new(
            request.SourceOperationRunId,
            request.TransitionId,
            request.TargetOperationId,
            request.SourceJudgement,
            request.Traversal,
            request.DecidedAtUtc);

    private static Application.Records.CreateTraceMaterialGenealogyRequest ToApplicationRequest(
        Models.CreateTraceMaterialGenealogyRequest request) => new(
        request.LinkId,
        request.ParentProductionUnitId,
        request.ChildProductionUnitId,
        request.Relationship,
        request.OperationId,
        request.LinkedBy,
        request.LinkedAtUtc);

    private static Application.Records.CreateTraceMaterialLocationTransitionRequest
        ToApplicationRequest(Models.CreateTraceMaterialLocationTransitionRequest request) => new(
            request.EvidenceId,
            request.ProductionRunId,
            request.MaterialKind,
            request.MaterialId,
            request.Source is null ? null : ToApplicationRequest(request.Source),
            request.Destination is null ? null : ToApplicationRequest(request.Destination),
            request.ActorId,
            request.OccurredAtUtc);

    private static Application.Records.CreateTraceMaterialLocationRequest ToApplicationRequest(
        Models.CreateTraceMaterialLocationRequest request) => new(
        request.Kind,
        request.LineId,
        request.StationSystemId,
        request.SlotId,
        request.CarrierId,
        request.CarrierPositionId);

    private static Application.Records.CreateTraceSlotOccupancyTransitionRequest
        ToApplicationRequest(Models.CreateTraceSlotOccupancyTransitionRequest request) => new(
            request.EvidenceId,
            request.ProductionRunId,
            request.LineId,
            request.StationSystemId,
            request.SlotId,
            request.MaterialKind,
            request.MaterialId,
            request.PreviousStatus,
            request.CurrentStatus,
            request.ActorId,
            request.OccurredAtUtc);

    private static Application.Records.CreateTraceDispositionTransitionRequest ToApplicationRequest(
        Models.CreateTraceDispositionTransitionRequest request) => new(
        request.EvidenceId,
        request.ProductionUnitId,
        request.ProductionRunId,
        request.PreviousDisposition,
        request.CurrentDisposition,
        request.Reason,
        request.ActorId,
        request.OccurredAtUtc);

    private static Application.Records.CreateTraceOperationOutputRequest ToApplicationRequest(
        Models.CreateTraceOperationOutputRequest request) =>
        new(request.Key, request.ValueKind, request.CanonicalJson);

    private static Application.Records.CreateTraceResourceFencingTokenRequest ToApplicationRequest(
        Models.CreateTraceResourceFencingTokenRequest request) =>
        new(request.ResourceKind, request.ResourceId, request.FencingToken);

    private static Application.Records.CreateTraceCommandRequest ToApplicationRequest(
        Models.CreateTraceCommandRequest request) =>
        new(
            request.RuntimeCommandId,
            request.RuntimeStepId,
            request.ActionId,
            request.TargetKind,
            request.TargetId,
            request.TargetCapabilityId,
            request.CommandName,
            request.Status,
            request.ResultJudgement,
            request.CreatedAtUtc,
            request.DeadlineAtUtc,
            request.AcceptedAtUtc,
            request.StartedAtUtc,
            request.CompletedAtUtc,
            request.ResultPayload,
            request.FailureReason);

    private static Application.Records.CreateMeasurementRecordRequest ToApplicationRequest(
        Models.CreateMeasurementRecordRequest request) =>
        new(
            request.MeasurementRecordId,
            request.Name,
            request.NumericValue,
            request.TextValue,
            request.Unit,
            request.DeviceId,
            request.RuntimeCommandId,
            request.ActionId,
            request.TargetKind,
            request.TargetId,
            request.CommandStatus,
            request.Passed,
            request.MeasuredAtUtc);

    private static Application.Records.CreateArtifactRecordRequest ToApplicationRequest(
        Models.CreateArtifactRecordRequest request) =>
        new(
            request.ArtifactRecordId,
            request.Name,
            request.Kind,
            request.StorageKey,
            request.MediaType,
            request.SizeBytes,
            request.Sha256,
            request.DeviceId,
            request.CapturedAtUtc);

    private static Application.Records.CreateTraceIncidentRequest ToApplicationRequest(
        Models.CreateTraceIncidentRequest request) =>
        new(request.RuntimeIncidentId, request.Severity, request.Code, request.Message, request.OccurredAtUtc);

    private static Application.Records.CreateAuditEntryRequest ToApplicationRequest(
        Models.CreateAuditEntryRequest request) =>
        new(request.AuditEntryId, request.ActorId, request.Action, request.Detail, request.OccurredAtUtc);

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
            command.Status,
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
            measurement.CommandStatus,
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
