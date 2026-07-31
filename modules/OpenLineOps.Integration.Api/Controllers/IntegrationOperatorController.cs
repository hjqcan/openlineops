using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Integration.Api.Mapping;
using OpenLineOps.Integration.Api.Models;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Queries;
using OpenLineOps.Integration.Application.WorkOrders;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.WorkOrders;

namespace OpenLineOps.Integration.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Integration)]
[Route("api/integration")]
[Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class IntegrationOperatorController(
    WorkOrderService workOrders,
    IIntegrationQueryStore queries) : IntegrationControllerBase
{
    [HttpGet("work-orders/{workOrderId}")]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderResponse>> GetWorkOrderAsync(
        string workOrderId,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await workOrders
                .GetAsync(new WorkOrderId(workOrderId), cancellationToken)
                .ConfigureAwait(false);
            return order is null
                ? NotFoundProblemResult("WorkOrder", workOrderId)
                : Ok(IntegrationApiMapper.ToResponse(order));
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
    }

    [HttpPost("work-orders/{workOrderId}/transitions")]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkOrderResponse>> TransitionWorkOrderAsync(
        string workOrderId,
        TransitionWorkOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Forbid();
        }

        try
        {
            var kind = ParseTransition(request.Kind);
            var order = await workOrders.TransitionAsync(
                    new TransitionWorkOrderCommand(
                        new WorkOrderId(workOrderId),
                        new WorkOrderFactId(request.FactId),
                        kind,
                        request.OccurredAtUtc,
                        actorId,
                        request.Reason),
                    cancellationToken)
                .ConfigureAwait(false);
            return Ok(IntegrationApiMapper.ToResponse(order));
        }
        catch (KeyNotFoundException)
        {
            return NotFoundProblemResult("WorkOrder", workOrderId);
        }
        catch (IntegrationMessageConflictException exception)
        {
            return ConflictProblemResult(exception);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ConflictProblemResult(exception);
        }
    }

    [HttpGet("work-requests/{workRequestId}")]
    [ProducesResponseType<WorkRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkRequestResponse>> GetWorkRequestAsync(
        string workRequestId,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await queries
                .GetWorkRequestAsync(
                    new WorkRequestId(workRequestId),
                    cancellationToken)
                .ConfigureAwait(false);
            return snapshot is null
                ? NotFoundProblemResult("WorkRequest", workRequestId)
                : Ok(IntegrationApiMapper.ToResponse(snapshot));
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
    }

    private static WorkOrderFactKind ParseTransition(string value)
    {
        return Enum.TryParse<WorkOrderFactKind>(
                   value,
                   ignoreCase: false,
                   out var parsed)
               && Enum.IsDefined(parsed)
               && parsed is not WorkOrderFactKind.Created
               && string.Equals(value, parsed.ToString(), StringComparison.Ordinal)
            ? parsed
            : throw new ArgumentException(
                $"Work order transition '{value}' is invalid.",
                nameof(value));
    }
}
