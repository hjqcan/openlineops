using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route("api/stations/{stationId}/lifecycle")]
public sealed class StationLifecycleController(StationLifecycleService service) : ControllerBase
{
    [HttpGet]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<StationLifecycleApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StationLifecycleApiResponse>> GetAsync(
        string stationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(
                await service.GetAsync(new StationId(stationId), cancellationToken)
                    .ConfigureAwait(false),
                created: false);
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPut]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
    [ProducesResponseType<StationLifecycleApiResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationLifecycleApiResponse>> CreateAsync(
        string stationId,
        CreateStationLifecycleApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(
                await service.CreateAsync(
                        new StationId(stationId),
                        ParseMode(request.Mode),
                        ToDomain(request.Readiness),
                        User.GetRequiredActorId(),
                        request.Reason,
                        cancellationToken)
                    .ConfigureAwait(false),
                created: true);
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("mode")]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
    [ProducesResponseType<StationLifecycleApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationLifecycleApiResponse>> ChangeModeAsync(
        string stationId,
        ChangeStationModeApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(
                await service.ChangeModeAsync(
                        new StationId(stationId),
                        ParseMode(request.Mode),
                        User.GetRequiredActorId(),
                        request.Reason,
                        cancellationToken)
                    .ConfigureAwait(false),
                created: false);
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("readiness")]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
    [ProducesResponseType<StationLifecycleApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationLifecycleApiResponse>> UpdateReadinessAsync(
        string stationId,
        UpdateStationReadinessApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsCallingStation(stationId))
        {
            return Forbid();
        }

        try
        {
            return ToActionResult(
                await service.UpdateReadinessAsync(
                        new StationId(stationId),
                        ToDomain(request.Readiness),
                        User.GetRequiredActorId(),
                        request.Reason,
                        cancellationToken)
                    .ConfigureAwait(false),
                created: false);
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("commands/acknowledge")]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
    [ProducesResponseType<StationLifecycleApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationLifecycleApiResponse>> AcknowledgeAsync(
        string stationId,
        StationLifecycleCommandApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsCallingStation(stationId))
        {
            return Forbid();
        }

        return await CommandCoreAsync(
                stationId,
                StationLifecycleCommand.Acknowledge,
                request,
                cancellationToken)
            .ConfigureAwait(false);
    }

    [HttpPost("commands/{command}")]
    [Microsoft.AspNetCore.Authorization.Authorize(
        Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<StationLifecycleApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationLifecycleApiResponse>> CommandAsync(
        string stationId,
        string command,
        StationLifecycleCommandApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseOperatorCommand(command, out var parsed))
        {
            return BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Validation.Runtime.StationLifecycleCommand",
                $"Station lifecycle command '{command}' is not supported."));
        }

        return await CommandCoreAsync(stationId, parsed, request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ActionResult<StationLifecycleApiResponse>> CommandCoreAsync(
        string stationId,
        StationLifecycleCommand command,
        StationLifecycleCommandApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToActionResult(
                await service.CommandAsync(
                        new StationId(stationId),
                        command,
                        User.GetRequiredActorId(),
                        request.Reason,
                        cancellationToken)
                    .ConfigureAwait(false),
                created: false);
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    private bool IsCallingStation(string stationId)
    {
        return string.Equals(
            User.GetRequiredStationId(),
            stationId,
            StringComparison.Ordinal);
    }

    private ActionResult<StationLifecycleApiResponse> ToActionResult(
        Result<StationLifecyclePersistenceEntry> result,
        bool created)
    {
        if (result.IsSuccess)
        {
            var response = ToResponse(result.Value);
            return created
                ? Created($"/api/stations/{response.StationId}/lifecycle", response)
                : Ok(response);
        }

        var error = result.Error;
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

    private ActionResult<StationLifecycleApiResponse> Validation(Exception exception)
    {
        ModelState.AddModelError(string.Empty, exception.Message);
        return ValidationProblem(ModelState);
    }

    private static StationLifecycleApiResponse ToResponse(
        StationLifecyclePersistenceEntry entry)
    {
        var station = entry.Station;
        return new StationLifecycleApiResponse(
            station.Id.Value,
            entry.Revision,
            station.Mode.ToString(),
            station.State.ToString(),
            ToResponse(station.Readiness),
            station.CreatedAtUtc,
            station.LastChangedAtUtc,
            station.TransitionAudit.Select(transition =>
                new StationTransitionAuditApiResponse(
                    transition.Sequence,
                    transition.FromState.ToString(),
                    transition.ToState.ToString(),
                    transition.Trigger.ToString(),
                    transition.Mode.ToString(),
                    transition.ActorId,
                    transition.Reason,
                    ToResponse(transition.Readiness),
                    transition.OccurredAtUtc))
                .ToArray());
    }

    private static StationReadinessApiModel ToResponse(StationReadiness readiness)
    {
        return new StationReadinessApiModel(
            readiness.InterlocksSatisfied,
            readiness.Homed,
            readiness.CriticalDevicesHealthy,
            readiness.RecipeVerified,
            readiness.CalibrationValid,
            readiness.SafetyPermitGranted);
    }

    private static StationReadiness ToDomain(StationReadinessApiModel readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        return new StationReadiness(
            readiness.InterlocksSatisfied,
            readiness.Homed,
            readiness.CriticalDevicesHealthy,
            readiness.RecipeVerified,
            readiness.CalibrationValid,
            readiness.SafetyPermitGranted);
    }

    private static StationMode ParseMode(string value)
    {
        return Enum.TryParse<StationMode>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
                ? parsed
                : throw new ArgumentException(
                    $"Station mode '{value}' is invalid.",
                    nameof(value));
    }

    private static bool TryParseOperatorCommand(
        string value,
        out StationLifecycleCommand command)
    {
        command = value switch
        {
            "reset" => StationLifecycleCommand.Reset,
            "start" => StationLifecycleCommand.Start,
            "complete" => StationLifecycleCommand.Complete,
            "hold" => StationLifecycleCommand.Hold,
            "unhold" => StationLifecycleCommand.Unhold,
            "suspend" => StationLifecycleCommand.Suspend,
            "unsuspend" => StationLifecycleCommand.Unsuspend,
            "stop" => StationLifecycleCommand.Stop,
            "abort" => StationLifecycleCommand.Abort,
            "clear" => StationLifecycleCommand.Clear,
            _ => default
        };
        return value is "reset"
            or "start"
            or "complete"
            or "hold"
            or "unhold"
            or "suspend"
            or "unsuspend"
            or "stop"
            or "abort"
            or "clear";
    }

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
