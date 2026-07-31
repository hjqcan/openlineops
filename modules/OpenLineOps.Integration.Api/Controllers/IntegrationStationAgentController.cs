using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Integration.Api.Mapping;
using OpenLineOps.Integration.Api.Models;
using OpenLineOps.Integration.Api.Transport;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Queries;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.Messages;

namespace OpenLineOps.Integration.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Integration)]
[Route("api/integration")]
[Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
public sealed class IntegrationStationAgentController(
    IdempotentWorkRequestService workRequests,
    IWorkRequestHandler handler,
    IIntegrationQueryStore queries,
    TimeProvider timeProvider) : IntegrationControllerBase
{
    [HttpPost("work-requests")]
    [ProducesResponseType<WorkRequestProcessingResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<WorkRequestProcessingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<WorkRequestProcessingResponse>> SubmitWorkRequestAsync(
        SubmitWorkRequestRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetStationId(out var stationId)
            || !string.Equals(stationId, request.StationId, StringComparison.Ordinal))
        {
            return Forbid();
        }

        if (handler is IWorkRequestHandlerReadiness readiness && !readiness.IsReady)
        {
            return UnavailableProblemResult(
                new IntegrationEndpointUnavailableException(
                    readiness.UnavailabilityReason
                    ?? "The work-request endpoint is not ready."));
        }

        try
        {
            if (request.Payload.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "A work request payload must be a JSON object.",
                    nameof(request));
            }

            var workRequest = new WorkRequest(
                new WorkRequestId(request.WorkRequestId),
                new WorkOrderId(request.WorkOrderId),
                ParseKind(request.Kind),
                WorkRequestStatus.Received,
                stationId,
                request.OccurredAtUtc,
                request.Payload.GetRawText());
            var result = await workRequests
                .ProcessAsync(
                    workRequest,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == WorkRequestProcessingOutcome.InProgress)
            {
                return Accepted(
                    $"/api/integration/work-requests/"
                    + Uri.EscapeDataString(request.WorkRequestId));
            }

            var response = result.Response
                ?? throw new InvalidDataException(
                    "A completed work request has no response.");
            var persisted = await queries
                .GetWorkResponseAsync(response.Id, cancellationToken)
                .ConfigureAwait(false);
            var body = new WorkRequestProcessingResponse(
                result.Outcome.ToString(),
                persisted is null
                    ? IntegrationApiMapper.ToResponse(response)
                    : IntegrationApiMapper.ToResponse(persisted));
            return result.Outcome == WorkRequestProcessingOutcome.Processed
                ? Created(
                    $"/api/integration/work-requests/"
                    + Uri.EscapeDataString(request.WorkRequestId),
                    body)
                : Ok(body);
        }
        catch (IntegrationMessageConflictException exception)
        {
            return ConflictProblemResult(exception);
        }
        catch (IntegrationInboxLeaseLostException exception)
        {
            return ConflictProblemResult(exception);
        }
        catch (IntegrationEndpointUnavailableException exception)
        {
            return UnavailableProblemResult(exception);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
        catch (JsonException exception)
        {
            return ValidationProblemResult(exception);
        }
    }

    [HttpGet("work-responses/{workResponseId}")]
    [ProducesResponseType<WorkResponseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkResponseResponse>> GetWorkResponseAsync(
        string workResponseId,
        CancellationToken cancellationToken)
    {
        if (!TryGetStationId(out var stationId))
        {
            return Forbid();
        }

        try
        {
            var snapshot = await queries
                .GetWorkResponseAsync(
                    new WorkResponseId(workResponseId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return NotFoundProblemResult("WorkResponse", workResponseId);
            }

            return string.Equals(
                    snapshot.Response.TargetSystem,
                    stationId,
                    StringComparison.Ordinal)
                ? Ok(IntegrationApiMapper.ToResponse(snapshot))
                : Forbid();
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
    }

    private static WorkRequestKind ParseKind(string value)
    {
        return Enum.TryParse<WorkRequestKind>(
                   value,
                   ignoreCase: false,
                   out var parsed)
               && Enum.IsDefined(parsed)
               && string.Equals(value, parsed.ToString(), StringComparison.Ordinal)
            ? parsed
            : throw new ArgumentException(
                $"Work request kind '{value}' is invalid.",
                nameof(value));
    }
}
