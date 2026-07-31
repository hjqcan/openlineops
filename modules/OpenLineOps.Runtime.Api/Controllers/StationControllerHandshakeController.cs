using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route("api/stations/{stationId}/lifecycle/controller-handshake")]
public sealed class StationControllerHandshakeController(
    StationControllerHandshakeService service,
    IClock clock,
    StationControllerHandshakeOptions options) : ControllerBase
{
    [HttpGet]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.SafetyConfirmationPolicy)]
    [ProducesResponseType<StationControllerHandshakeApiResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StationControllerHandshakeApiResponse>> GetAsync(
        string stationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(
                await service.GetAsync(new StationId(stationId), cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpGet("facts")]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.SafetyConfirmationPolicy)]
    [ProducesResponseType<StationControllerHandshakeFactsApiResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StationControllerHandshakeFactsApiResponse>>
        ListFactsAsync(
            string stationId,
            CancellationToken cancellationToken)
    {
        try
        {
            var parsedStationId = new StationId(stationId);
            var result = await service.ListFactsAsync(
                    parsedStationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Error(result.Error);
            }

            return Ok(new StationControllerHandshakeFactsApiResponse(
                parsedStationId.Value,
                result.Value.Select(ToResponse).ToArray()));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
    [ProducesResponseType<StationControllerHandshakeApiResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationControllerHandshakeApiResponse>> ReportAsync(
        string stationId,
        ReportStationControllerHandshakeApiRequest request,
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
            return ToActionResult(
                await service.ReportAsync(
                        new StationId(stationId),
                        new StationControllerHandshakeInput(
                            request.OwnerInstanceId,
                            request.AgentFencingToken,
                            GetRequiredLeaseHandle(),
                            request.ControllerSessionId,
                            request.HeartbeatSequence,
                            request.CommandSequence,
                            request.AcknowledgedCommandSequence,
                            request.Busy,
                            request.Completed,
                            request.Error,
                            request.ErrorCode,
                            request.RecipeConfirmed,
                            request.ConfirmedRecipeId,
                            request.ConfirmedRecipeVersion,
                            request.SafetyPermitGranted,
                            request.SourceTimestampUtc,
                            request.CommandId,
                            request.CommandFencingToken,
                            ParseEnum<StationMode>(
                                request.ObservedMode,
                                nameof(request.ObservedMode)),
                            ParseEnum<StationState>(
                                request.ObservedState,
                                nameof(request.ObservedState)),
                            request.StateSequence),
                        User.GetRequiredActorId(),
                        request.Reason,
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    private string GetRequiredLeaseHandle()
    {
        var values = Request.Headers[
            StationAgentControlLeaseController.LeaseHandleHeader];
        return values.Count == 1
            ? values[0]!
            : throw new ArgumentException(
                $"{StationAgentControlLeaseController.LeaseHandleHeader} "
                + "must be supplied exactly once.");
    }

    [HttpPost("recovery/acknowledge")]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.SafetyConfirmationPolicy)]
    [ProducesResponseType<StationControllerHandshakeApiResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationControllerHandshakeApiResponse>>
        AcknowledgeRecoveryAsync(
            string stationId,
            AcknowledgeStationControllerRecoveryApiRequest request,
            CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(
                await service.AcknowledgeRecoveryAsync(
                        new StationId(stationId),
                        request.ExpectedRecoveryEpoch,
                        request.ControllerSessionId,
                        request.OperationalStateSha256,
                        User.GetRequiredActorId(),
                        request.Reason,
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    private ActionResult<StationControllerHandshakeApiResponse> ToActionResult(
        Result<StationControllerHandshakePersistenceEntry> result)
    {
        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : Error(result.Error);
    }

    private StationControllerHandshakeApiResponse ToResponse(
        StationControllerHandshakePersistenceEntry entry)
    {
        var state = entry.State;
        var readiness = StationControllerHandshakeReadinessEvaluator.Evaluate(
            state,
            clock.UtcNow,
            options);
        return new StationControllerHandshakeApiResponse(
            state.StationId.Value,
            entry.Revision,
            state.RecoveryRequired,
            state.RecoveryEpoch,
            state.OperationalEpoch,
            StationControllerHandshakeEvidence.OperationalStateSha256(state),
            readiness.Allowed,
            readiness.Code,
            readiness.Reason,
            state.CreatedAtUtc,
            state.LastChangedAtUtc,
            ToResponse(state.LatestReport));
    }

    private ActionResult Error(ApplicationError error)
    {
        if (error.Code.StartsWith("NotFound.", StringComparison.Ordinal))
        {
            return NotFound(Problem(
                StatusCodes.Status404NotFound,
                error.Code,
                error.Message));
        }

        if (error.Code.StartsWith("Validation.", StringComparison.Ordinal))
        {
            return BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                error.Code,
                error.Message));
        }

        return Conflict(Problem(
            StatusCodes.Status409Conflict,
            error.Code,
            error.Message));
    }

    private ActionResult Validation(Exception exception)
    {
        ModelState.AddModelError(string.Empty, exception.Message);
        return ValidationProblem(ModelState);
    }

    private static StationControllerHandshakeReportApiResponse ToResponse(
        StationControllerHandshakeReport report) =>
        new(
            report.OwnerAgentId,
            report.OwnerAgentInstanceId,
            report.AgentFencingToken,
            report.ControllerSessionId,
            report.HeartbeatSequence,
            report.CommandSequence,
            report.AcknowledgedCommandSequence,
            report.CommandId,
            report.CommandFencingToken,
            report.ObservedMode.ToString(),
            report.ObservedState.ToString(),
            report.StateSequence,
            report.Busy,
            report.Completed,
            report.Error,
            report.ErrorCode,
            report.RecipeConfirmed,
            report.ConfirmedRecipeId,
            report.ConfirmedRecipeVersion,
            report.SafetyPermitGranted,
            report.SourceTimestampUtc,
            report.ReceivedAtUtc);

    private static TEnum ParseEnum<TEnum>(
        string value,
        string parameterName)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed
            : throw new ArgumentException(
                $"{parameterName} '{value}' is invalid.",
                parameterName);

    private static StationControllerHandshakeFactApiResponse ToResponse(
        StationControllerHandshakeFact fact) =>
        new(
            fact.Sequence,
            fact.Kind.ToString(),
            fact.RecoveryRequired,
            fact.ActorId,
            fact.Reason,
            fact.OccurredAtUtc,
            ToResponse(fact.Report));

    private static ProblemDetails Problem(int status, string title, string detail)
    {
        return new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        };
    }
}
