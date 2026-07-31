using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Commissioning.Api.Mapping;
using OpenLineOps.Commissioning.Api.Models;
using OpenLineOps.Commissioning.Application.Contracts;
using OpenLineOps.Commissioning.Application.Services;
using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Commissioning)]
[Route("api/stations/{stationId}/commissioning/sessions")]
[Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
public sealed class CommissioningStationAgentController(
    ICommissioningService commissioningService) : ControllerBase
{
    [HttpPost("{sessionId}/recovery/interrupted-action")]
    [ProducesResponseType<CommissioningSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CommissioningSessionResponse>>
        RecoverInterruptedActionAsync(
            string stationId,
            string sessionId,
            RecoverCommissioningActionRequest request,
            CancellationToken cancellationToken)
    {
        if (!string.Equals(
                User.GetRequiredStationId(),
                stationId,
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        try
        {
            var session = await commissioningService
                .GetAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (session.IsFailure)
            {
                return CommissioningApiMapper.ToProblem(session.Error);
            }

            if (!string.Equals(
                    session.Value.StationId,
                    stationId,
                    StringComparison.Ordinal))
            {
                return CommissioningApiMapper.ToProblem(
                    ApplicationError.NotFound(
                        "Commissioning.SessionNotFound",
                        $"Commissioning session {sessionId} was not found for "
                        + $"station {stationId}."));
            }

            if (session.Value.FencingToken != request.FencingToken)
            {
                return CommissioningApiMapper.ToProblem(
                    ApplicationError.Conflict(
                        "Commissioning.StaleFencingToken",
                        $"Recovery fencing token {request.FencingToken} does not "
                        + $"match active lease token {session.Value.FencingToken}."));
            }

            var result = await commissioningService.RecoverInterruptedActionAsync(
                    new RecoverCommissioningActionCommand(
                        sessionId,
                        request.ActionId,
                        ParseIdempotency(request.IdempotencyClass)),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.IsSuccess
                ? Ok(CommissioningApiMapper.ToResponse(result.Value))
                : CommissioningApiMapper.ToProblem(result.Error);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(CommissioningApiMapper.Problem(
                StatusCodes.Status400BadRequest,
                "Validation.Commissioning.Request",
                exception.Message));
        }
    }

    private static CommissioningActionIdempotencyClass ParseIdempotency(
        string value)
    {
        return Enum.TryParse<CommissioningActionIdempotencyClass>(
                value,
                ignoreCase: false,
                out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
                ? parsed
                : throw new ArgumentException(
                    $"Idempotency class '{value}' is invalid.",
                    nameof(value));
    }
}
