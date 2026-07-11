using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
public sealed class ProductionOperationsController(
    IProductionRunRepository repository,
    IClock clock) : ControllerBase
{
    [HttpGet(OpenLineOpsApiRoutes.OperationsActiveRuns)]
    [ProducesResponseType<ActiveProductionRunsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ActiveProductionRunsResponse>> GetActiveRunsAsync(
        [FromQuery] string? productionLineDefinitionId,
        [FromQuery] string? stationSystemId,
        [FromQuery] string? slotId,
        CancellationToken cancellationToken)
    {
        var active = await repository.ListActiveAsync(
                productionLineDefinitionId,
                stationSystemId,
                slotId,
                cancellationToken)
            .ConfigureAwait(false);
        return Ok(new ActiveProductionRunsResponse(active
            .Select(entry => ProductionRunResponseMapper.ToResponse(entry.Run.ToSnapshot()))
            .ToArray()));
    }

    [HttpGet(OpenLineOpsApiRoutes.OperationsLineState)]
    [ProducesResponseType<ProductionLineRuntimeStateResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductionLineRuntimeStateResponse>> GetLineStateAsync(
        string lineId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lineId)
            || char.IsWhiteSpace(lineId[0])
            || char.IsWhiteSpace(lineId[^1]))
        {
            return BadRequest();
        }

        var active = await repository.ListActiveAsync(
                productionLineDefinitionId: lineId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var runs = active
            .Select(entry => ProductionRunResponseMapper.ToResponse(entry.Run.ToSnapshot()))
            .ToArray();
        return Ok(new ProductionLineRuntimeStateResponse(
            lineId,
            clock.UtcNow,
            runs.Length,
            runs));
    }
}
