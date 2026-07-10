using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.RuntimeV1)]
[Route(OpenLineOpsApiRoutes.RuntimeProductionRuns)]
public sealed class ProductionRunsController(IProductionRunRepository repository) : ControllerBase
{
    [HttpGet("{productionRunId}")]
    [ProducesResponseType<ProductionRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductionRunResponse>> GetByIdAsync(
        string productionRunId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(productionRunId, "D", out var parsedProductionRunId)
            || parsedProductionRunId == Guid.Empty
            || !string.Equals(
                parsedProductionRunId.ToString("D"),
                productionRunId,
                StringComparison.Ordinal))
        {
            return BadRequest();
        }

        var entry = await repository
            .GetByIdAsync(new ProductionRunId(parsedProductionRunId), cancellationToken)
            .ConfigureAwait(false);

        return entry is null
            ? NotFound()
            : Ok(ProductionRunResponseMapper.ToResponse(entry.Run.ToSnapshot()));
    }
}
