using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Api.Models;
using OpenLineOps.Traceability.Application.ReadModels;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Traceability)]
[Route(OpenLineOpsApiRoutes.Traceability + "/read-models")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class TraceReadModelsController : ControllerBase
{
    private readonly ITraceReadModelService _readModelService;

    public TraceReadModelsController(ITraceReadModelService readModelService)
    {
        _readModelService = readModelService;
    }

    [HttpGet("station-dashboard")]
    [ProducesResponseType<StationTraceDashboardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StationTraceDashboardResponse>> GetStationDashboardAsync(
        [FromQuery] string? stationSystemId,
        [FromQuery] DateTimeOffset? completedFromUtc,
        [FromQuery] DateTimeOffset? completedToUtc,
        [FromQuery] int recentLimit = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _readModelService.GetStationDashboardAsync(
            new StationTraceDashboardQuery(
                stationSystemId,
                completedFromUtc,
                completedToUtc,
                recentLimit),
            cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    [HttpGet("engineering-search")]
    [ProducesResponseType<EngineeringTraceSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EngineeringTraceSearchResponse>> SearchForEngineeringAsync(
        [FromQuery] Guid? productionRunId,
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
        var result = await _readModelService.SearchForEngineeringAsync(
            new EngineeringTraceSearchQuery(
                productionRunId,
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
        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    private static StationTraceDashboardResponse ToResponse(StationTraceDashboardReadModel dashboard) =>
        new(
            dashboard.StationSystemId,
            dashboard.CompletedFromUtc,
            dashboard.CompletedToUtc,
            dashboard.TotalCount,
            dashboard.PassedCount,
            dashboard.FailedCount,
            dashboard.AbortedCount,
            dashboard.UnknownCount,
            dashboard.NotApplicableCount,
            dashboard.FirstCompletedAtUtc,
            dashboard.LastCompletedAtUtc,
            dashboard.IsWindowTruncated,
            dashboard.RecentTraces.Select(ToResponse).ToArray());

    private static StationRecentTraceResponse ToResponse(StationRecentTraceReadModel trace) =>
        new(
            trace.TraceRecordId,
            trace.ProductionRunId,
            trace.ProjectId,
            trace.ApplicationId,
            trace.ProjectSnapshotId,
            trace.TopologyId,
            trace.ProductionLineDefinitionId,
            trace.ProductModelId,
            trace.ProductionUnitIdentityInputKey,
            trace.ProductionUnitIdentityValue,
            trace.LotId,
            trace.CarrierId,
            trace.ExecutionStatus,
            trace.Judgement,
            trace.Disposition,
            trace.CompletedAtUtc,
            trace.OperationCount,
            trace.CommandCount,
            trace.FailedCommandCount,
            trace.MeasurementCount,
            trace.FailedMeasurementCount,
            trace.ArtifactCount,
            trace.IncidentCount,
            trace.GenealogyCount,
            trace.MaterialLocationTransitionCount,
            trace.SlotOccupancyTransitionCount,
            trace.DispositionTransitionCount);

    private static EngineeringTraceSearchResponse ToResponse(EngineeringTraceSearchReadModel search) =>
        new(
            new PagedEngineeringTraceSearchRowsResponse(
                search.Results.Items.Select(ToResponse).ToArray(),
                search.Results.PageNumber,
                search.Results.PageSize,
                search.Results.TotalCount,
                search.Results.TotalPages),
            ToResponse(search.Facets),
            search.AreFacetsTruncated);

    private static EngineeringTraceSearchRowResponse ToResponse(EngineeringTraceSearchRowReadModel row) =>
        new(
            row.TraceRecordId,
            row.ProductionRunId,
            row.ProjectId,
            row.ApplicationId,
            row.ProjectSnapshotId,
            row.TopologyId,
            row.ProductionLineDefinitionId,
            row.ProductModelId,
            row.ProductionUnitIdentityInputKey,
            row.ProductionUnitIdentityValue,
            row.LotId,
            row.CarrierId,
            row.ActorId,
            row.ExecutionStatus,
            row.Judgement,
            row.Disposition,
            row.CreatedAtUtc,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.OperationCount,
            row.FailedOperationCount,
            row.CommandCount,
            row.FailedCommandCount,
            row.MeasurementCount,
            row.FailedMeasurementCount,
            row.ArtifactCount,
            row.IncidentCount,
            row.RouteDecisionCount,
            row.GenealogyCount,
            row.MaterialLocationTransitionCount,
            row.SlotOccupancyTransitionCount,
            row.DispositionTransitionCount);

    private static EngineeringTraceSearchFacetsResponse ToResponse(
        EngineeringTraceSearchFacetsReadModel facets) =>
        new(
            facets.Judgements.Select(ToResponse).ToArray(),
            facets.ExecutionStatuses.Select(ToResponse).ToArray(),
            facets.Dispositions.Select(ToResponse).ToArray(),
            facets.StationSystems.Select(ToResponse).ToArray(),
            facets.Devices.Select(ToResponse).ToArray(),
            facets.ProductionLines.Select(ToResponse).ToArray(),
            facets.ProcessVersions.Select(ToResponse).ToArray(),
            facets.ProjectSnapshots.Select(ToResponse).ToArray());

    private static TraceFacetCountResponse ToResponse(TraceFacetCountReadModel facet) =>
        new(facet.Value, facet.Count);

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
