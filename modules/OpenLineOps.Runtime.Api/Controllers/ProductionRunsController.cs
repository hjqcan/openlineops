using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route(OpenLineOpsApiRoutes.ProductionRuns)]
public sealed class ProductionRunsController(
    IProductionRunRepository repository,
    IProductionRunCoordinator coordinator) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ProductionRunResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionRunResponse>> CreateAsync(
        CreateProductionRunRequest request,
        CancellationToken cancellationToken)
    {
        SubmitProductionRunRequest applicationRequest;
        try
        {
            applicationRequest = ProductionRunRequestMapper.ToApplication(request);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }

        var result = await coordinator.SubmitAsync(applicationRequest, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.Code.StartsWith("Conflict.", StringComparison.Ordinal)
                ? Conflict(new ProblemDetails
                {
                    Title = result.Error.Code,
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status409Conflict
                })
                : BadRequest(new ProblemDetails
                {
                    Title = result.Error.Code,
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status400BadRequest
                });
        }

        var response = ProductionRunResponseMapper.ToResponse(result.Value);
        return AcceptedAtAction(
            nameof(GetByIdAsync),
            new { productionRunId = response.ProductionRunId.ToString("D") },
            response);
    }

    [HttpGet("{productionRunId}")]
    [ProducesResponseType<ProductionRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductionRunResponse>> GetByIdAsync(
        string productionRunId,
        CancellationToken cancellationToken)
    {
        if (!TryParseRunId(productionRunId, out var runId))
        {
            return BadRequest();
        }

        var entry = await repository.GetByIdAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        return entry is null
            ? NotFound()
            : Ok(ProductionRunResponseMapper.ToResponse(entry.Run.ToSnapshot()));
    }

    [HttpPost("{productionRunId}/commands/{command}")]
    [ProducesResponseType<ProductionRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionRunResponse>> CommandAsync(
        string productionRunId,
        string command,
        ProductionRunCommandApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseRunId(productionRunId, out var runId)
            || !TryParseCommand(command, out var parsedCommand))
        {
            return BadRequest();
        }

        ProductionRunCommandRequest applicationRequest;
        try
        {
            applicationRequest = new ProductionRunCommandRequest(
                parsedCommand,
                request.ActorId,
                request.Reason,
                request.OperationId);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }

        var result = await coordinator.CommandAsync(runId, applicationRequest, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Ok(ProductionRunResponseMapper.ToResponse(result.Value));
        }

        var problem = new ProblemDetails { Title = result.Error.Code, Detail = result.Error.Message };
        if (result.Error.Code.StartsWith("NotFound.", StringComparison.Ordinal))
        {
            problem.Status = StatusCodes.Status404NotFound;
            return NotFound(problem);
        }

        problem.Status = StatusCodes.Status409Conflict;
        return Conflict(problem);
    }

    private static bool TryParseRunId(string value, out ProductionRunId runId)
    {
        if (Guid.TryParseExact(value, "D", out var parsed)
            && parsed != Guid.Empty
            && string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            runId = new ProductionRunId(parsed);
            return true;
        }

        runId = default;
        return false;
    }

    private static bool TryParseCommand(string value, out ProductionRunCommand command) =>
        Enum.TryParse(value, ignoreCase: false, out command)
        && Enum.IsDefined(command)
        && string.Equals(command.ToString(), value, StringComparison.Ordinal);
}
