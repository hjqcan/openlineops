using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Integration.Api.Mapping;
using OpenLineOps.Integration.Api.Models;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Application.Queries;
using OpenLineOps.Integration.Application.WorkOrders;
using OpenLineOps.Integration.Domain.Identifiers;

namespace OpenLineOps.Integration.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Integration)]
[Route("api/integration")]
[Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
public sealed class IntegrationEngineeringController(
    WorkOrderService workOrders,
    IIntegrationQueryStore queries,
    IIntegrationOutboxStore outbox,
    IntegrationOutboxReplayService replayService,
    TimeProvider timeProvider) : IntegrationControllerBase
{
    [HttpPost("work-orders")]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkOrderResponse>> CreateWorkOrderAsync(
        CreateWorkOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Forbid();
        }

        try
        {
            var order = await workOrders.CreateAsync(
                    new CreateWorkOrderCommand(
                        new WorkOrderId(request.WorkOrderId),
                        request.ProductModelId,
                        request.TargetQuantity,
                        new WorkOrderFactId(request.FactId),
                        request.OccurredAtUtc,
                        actorId),
                    cancellationToken)
                .ConfigureAwait(false);
            var response = IntegrationApiMapper.ToResponse(order);
            return Created(
                $"/api/integration/work-orders/{Uri.EscapeDataString(response.WorkOrderId)}",
                response);
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

    [HttpGet("outbox/dead-letters")]
    [ProducesResponseType<IReadOnlyList<DeadLetterResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<DeadLetterResponse>>> ListDeadLettersAsync(
        [FromQuery] int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var deadLetters = await queries
                .ListDeadLettersAsync(maximumCount, cancellationToken)
                .ConfigureAwait(false);
            return Ok(deadLetters.Select(static item => new DeadLetterResponse(
                    item.Sequence,
                    item.MessageId,
                    item.CorrelationId,
                    item.AttemptCount,
                    item.LastError,
                    item.CreatedAtUtc,
                    item.DeadLetteredAtUtc))
                .ToArray());
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return ValidationProblemResult(exception);
        }
    }

    [HttpPost("outbox/{messageId}/replay")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReplayDeadLetterAsync(
        string messageId,
        ReplayOutboxRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Forbid();
        }

        try
        {
            await replayService.ReplayAsync(
                    new ManualOutboxReplayRequest(
                        messageId,
                        actorId,
                        request.Reason,
                        timeProvider.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
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

    [HttpGet("outbox/{messageId}/replay-audit")]
    [ProducesResponseType<IReadOnlyList<OutboxReplayAuditResponse>>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<OutboxReplayAuditResponse>>>
        ListReplayAuditAsync(
            string messageId,
            CancellationToken cancellationToken)
    {
        try
        {
            var audit = await outbox
                .ListReplayAuditAsync(messageId, cancellationToken)
                .ConfigureAwait(false);
            return Ok(audit.Select(static item => new OutboxReplayAuditResponse(
                    item.Sequence,
                    item.MessageId,
                    item.ActorId,
                    item.Reason,
                    item.ReplayedAtUtc,
                    item.PreviousAttemptCount))
                .ToArray());
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
    }
}
