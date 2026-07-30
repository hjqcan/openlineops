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
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class ProductionOperationsController(
    IProductionRunRepository repository,
    IProductionLineRuntimeStateReader lineStateReader) : ControllerBase
{
    private static readonly HashSet<string> ActiveRunQueryFields =
        new(StringComparer.Ordinal)
        {
            "productionLineDefinitionId",
            "stationSystemId",
            "slotResourceId"
        };

    [HttpGet(OpenLineOpsApiRoutes.OperationsActiveRuns)]
    [ProducesResponseType<ActiveProductionRunsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ActiveProductionRunsResponse>> GetActiveRunsAsync(
        [FromQuery] string? productionLineDefinitionId,
        [FromQuery] string? stationSystemId,
        [FromQuery] string? slotResourceId,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(key => !ActiveRunQueryFields.Contains(key))
            || Request.Query.Any(parameter => parameter.Value.Count != 1))
        {
            return BadRequest(StrictQueryProblem());
        }

        ProductionRunActiveQuery query;
        try
        {
            query = new ProductionRunActiveQuery(
                productionLineDefinitionId,
                stationSystemId,
                slotResourceId);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(exception.ParamName ?? "query", exception.Message);
            return ValidationProblem(ModelState);
        }

        var active = await repository.ListActiveAsync(
                query,
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

    private static ProblemDetails StrictQueryProblem() => new()
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Validation.StrictQuery",
        Detail = "Active Production Run query contains an unknown or repeated field."
    };
}
