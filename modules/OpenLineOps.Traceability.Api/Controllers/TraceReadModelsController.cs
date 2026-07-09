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
        var result = await _readModelService
            .GetStationDashboardAsync(
                new StationTraceDashboardQuery(
                    stationId,
                    completedFromUtc,
                    completedToUtc,
                    recentLimit),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpGet("engineering-search")]
    [ProducesResponseType<EngineeringTraceSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EngineeringTraceSearchResponse>> SearchForEngineeringAsync(
        [FromQuery] string? serialNumber,
        [FromQuery] string? batchId,
        [FromQuery] string? stationId,
        [FromQuery] string? fixtureId,
        [FromQuery] string? processDefinitionId,
        [FromQuery] string? processVersionId,
        [FromQuery] string? configurationSnapshotId,
        [FromQuery] string? recipeSnapshotId,
        [FromQuery] string? deviceId,
        [FromQuery] string? judgement,
        [FromQuery] string? projectId,
        [FromQuery] string? applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? topologyId,
        [FromQuery] DateTimeOffset? completedFromUtc,
        [FromQuery] DateTimeOffset? completedToUtc,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _readModelService
            .SearchForEngineeringAsync(
                new EngineeringTraceSearchQuery(
                    serialNumber,
                    batchId,
                    stationId,
                    fixtureId,
                    processDefinitionId,
                    processVersionId,
                    configurationSnapshotId,
                    recipeSnapshotId,
                    deviceId,
                    judgement,
                    completedFromUtc,
                    completedToUtc,
                    new PagedRequest(pageNumber, pageSize),
                    projectId,
                    applicationId,
                    projectSnapshotId,
                    topologyId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    private static StationTraceDashboardResponse ToResponse(StationTraceDashboardReadModel dashboard)
    {
        return new StationTraceDashboardResponse(
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
    }

    private static StationRecentTraceResponse ToResponse(StationRecentTraceReadModel trace)
    {
        return new StationRecentTraceResponse(
            trace.TraceRecordId,
            trace.RuntimeSessionId,
            trace.ProjectId,
            trace.ApplicationId,
            trace.ProjectSnapshotId,
            trace.TopologyId,
            trace.SerialNumber,
            trace.BatchId,
            trace.FixtureId,
            trace.ProcessVersionId,
            trace.DeviceId,
            trace.Judgement,
            trace.CompletedAtUtc,
            trace.MeasurementCount,
            trace.FailedMeasurementCount,
            trace.ArtifactCount);
    }

    private static EngineeringTraceSearchResponse ToResponse(EngineeringTraceSearchReadModel search)
    {
        return new EngineeringTraceSearchResponse(
            new PagedEngineeringTraceSearchRowsResponse(
                search.Results.Items.Select(ToResponse).ToArray(),
                search.Results.PageNumber,
                search.Results.PageSize,
                search.Results.TotalCount,
                search.Results.TotalPages),
            ToResponse(search.Facets),
            search.AreFacetsTruncated);
    }

    private static EngineeringTraceSearchRowResponse ToResponse(EngineeringTraceSearchRowReadModel row)
    {
        return new EngineeringTraceSearchRowResponse(
            row.TraceRecordId,
            row.RuntimeSessionId,
            row.ProjectId,
            row.ApplicationId,
            row.ProjectSnapshotId,
            row.TopologyId,
            row.SerialNumber,
            row.BatchId,
            row.StationId,
            row.FixtureId,
            row.ProcessDefinitionId,
            row.ProcessVersionId,
            row.ConfigurationSnapshotId,
            row.RecipeSnapshotId,
            row.DeviceId,
            row.Judgement,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.MeasurementCount,
            row.FailedMeasurementCount,
            row.ArtifactCount);
    }

    private static EngineeringTraceSearchFacetsResponse ToResponse(
        EngineeringTraceSearchFacetsReadModel facets)
    {
        return new EngineeringTraceSearchFacetsResponse(
            facets.Judgements.Select(ToResponse).ToArray(),
            facets.Stations.Select(ToResponse).ToArray(),
            facets.Devices.Select(ToResponse).ToArray(),
            facets.ProcessVersions.Select(ToResponse).ToArray(),
            facets.ProjectSnapshots.Select(ToResponse).ToArray());
    }

    private static TraceFacetCountResponse ToResponse(TraceFacetCountReadModel facet)
    {
        return new TraceFacetCountResponse(facet.Value, facet.Count);
    }

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode);
    }
}
