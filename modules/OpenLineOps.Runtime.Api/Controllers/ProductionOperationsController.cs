using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
public sealed class ProductionOperationsController(
    IProductionRunRepository repository,
    IProductionLineRuntimeStateReader lineStateReader) : ControllerBase
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
            .Select(entry => ProductionRunReadModelMapper.ToReadModel(entry.Run.ToSnapshot()))
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

        var state = await lineStateReader.ReadAsync(lineId, cancellationToken)
            .ConfigureAwait(false);
        return Ok(ProductionLineRuntimeStateResponseMapper.ToResponse(state));
    }
}
