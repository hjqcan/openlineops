using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Api.Models;
using OpenLineOps.Traceability.Application.ReadModels;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.TraceabilityV1)]
[Route(OpenLineOpsApiRoutes.Traceability + "/read-models")]
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
        [FromQuery] string? stationId,
        [FromQuery] DateTimeOffset? completedFromUtc,
        [FromQuery] DateTimeOffset? completedToUtc,
        [FromQuery] int recentLimit = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _readModelService.GetStationDashboardAsync(
            new StationTraceDashboardQuery(
                stationId,
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
        [FromQuery] string? dutModelId,
        [FromQuery] string? dutIdentityInputKey,
        [FromQuery] string? dutIdentityValue,
        [FromQuery] string? batchId,
        [FromQuery] string? fixtureId,
        [FromQuery] string? deviceId,
        [FromQuery] string? actorId,
        [FromQuery] string? runStatus,
        [FromQuery] string? judgement,
        [FromQuery] string? projectId,
        [FromQuery] string? applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? topologyId,
        [FromQuery] string? productionLineDefinitionId,
        [FromQuery] string? stageId,
        [FromQuery] string? workstationId,
        [FromQuery] string? stationId,
        [FromQuery] string? processDefinitionId,
        [FromQuery] string? processVersionId,
        [FromQuery] string? configurationSnapshotId,
        [FromQuery] string? recipeSnapshotId,
        [FromQuery] DateTimeOffset? completedFromUtc,
        [FromQuery] DateTimeOffset? completedToUtc,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _readModelService.SearchForEngineeringAsync(
            new EngineeringTraceSearchQuery(
                productionRunId,
                dutModelId,
                dutIdentityInputKey,
                dutIdentityValue,
                batchId,
                fixtureId,
                deviceId,
                actorId,
                runStatus,
                judgement,
                projectId,
                applicationId,
                projectSnapshotId,
                topologyId,
                productionLineDefinitionId,
                stageId,
                workstationId,
                stationId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                recipeSnapshotId,
                completedFromUtc,
                completedToUtc,
                new PagedRequest(pageNumber, pageSize)),
            cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    private static StationTraceDashboardResponse ToResponse(StationTraceDashboardReadModel dashboard) =>
        new(
            dashboard.StationId,
            dashboard.CompletedFromUtc,
            dashboard.CompletedToUtc,
            dashboard.TotalCount,
            dashboard.PassedCount,
            dashboard.FailedCount,
            dashboard.AbortedCount,
            dashboard.UnknownCount,
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
            trace.DutModelId,
            trace.DutIdentityInputKey,
            trace.DutIdentityValue,
            trace.BatchId,
            trace.FixtureId,
            trace.DeviceId,
            trace.RunStatus,
            trace.Judgement,
            trace.CompletedAtUtc,
            trace.StageCount,
            trace.CommandCount,
            trace.FailedCommandCount,
            trace.MeasurementCount,
            trace.FailedMeasurementCount,
            trace.ArtifactCount,
            trace.IncidentCount);

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
            row.DutModelId,
            row.DutIdentityInputKey,
            row.DutIdentityValue,
            row.BatchId,
            row.FixtureId,
            row.DeviceId,
            row.ActorId,
            row.RunStatus,
            row.Judgement,
            row.CreatedAtUtc,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.StageCount,
            row.FailedStageCount,
            row.CommandCount,
            row.FailedCommandCount,
            row.MeasurementCount,
            row.FailedMeasurementCount,
            row.ArtifactCount,
            row.IncidentCount);

    private static EngineeringTraceSearchFacetsResponse ToResponse(
        EngineeringTraceSearchFacetsReadModel facets) =>
        new(
            facets.Judgements.Select(ToResponse).ToArray(),
            facets.RunStatuses.Select(ToResponse).ToArray(),
            facets.Stations.Select(ToResponse).ToArray(),
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
