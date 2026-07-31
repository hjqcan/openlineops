using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
[Route("api/stations/{stationId}/agent-control-lease")]
public sealed class StationAgentControlLeaseController(
    StationAgentControlLeaseService service) : ControllerBase
{
    public const string LeaseHandleHeader = "X-OpenLineOps-Agent-Lease";

    [HttpGet]
    [ProducesResponseType<StationAgentControlLeaseStatusApiResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StationAgentControlLeaseStatusApiResponse>> GetAsync(
        string stationId,
        CancellationToken cancellationToken)
    {
        if (!IsCallingStation(stationId))
        {
            return Forbid();
        }

        try
        {
            var result = await service.GetAsync(
                    new StationId(stationId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return NotFound(Problem(
                    StatusCodes.Status404NotFound,
                    result.Error.Code,
                    result.Error.Message));
            }

            var observation = result.Value;
            return Ok(new StationAgentControlLeaseStatusApiResponse(
                stationId,
                observation.IsActive,
                observation.Lease!.ExpiresAtUtc,
                observation.ObservedAtUtc));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("acquire")]
    [ProducesResponseType<StationAgentControlLeaseApiResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationAgentControlLeaseApiResponse>> AcquireAsync(
        string stationId,
        AcquireStationAgentControlLeaseApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsCallingStation(stationId))
        {
            return Forbid();
        }

        try
        {
            return ToActionResult(
                await service.AcquireAsync(
                        new StationId(stationId),
                        User.GetRequiredActorId(),
                        request.OwnerInstanceId,
                        GetRequiredLeaseHandle(),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("renew")]
    [ProducesResponseType<StationAgentControlLeaseApiResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationAgentControlLeaseApiResponse>> RenewAsync(
        string stationId,
        RenewStationAgentControlLeaseApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsCallingStation(stationId))
        {
            return Forbid();
        }

        try
        {
            return ToActionResult(
                await service.RenewAsync(
                        new StationId(stationId),
                        User.GetRequiredActorId(),
                        request.OwnerInstanceId,
                        request.FencingToken,
                        GetRequiredLeaseHandle(),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    [HttpPost("release")]
    [ProducesResponseType<StationAgentControlLeaseApiResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationAgentControlLeaseApiResponse>> ReleaseAsync(
        string stationId,
        ReleaseStationAgentControlLeaseApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsCallingStation(stationId))
        {
            return Forbid();
        }

        try
        {
            return ToActionResult(
                await service.ReleaseAsync(
                        new StationId(stationId),
                        User.GetRequiredActorId(),
                        request.OwnerInstanceId,
                        request.FencingToken,
                        GetRequiredLeaseHandle(),
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Validation(exception);
        }
    }

    private bool IsCallingStation(string stationId) => string.Equals(
        User.GetRequiredStationId(),
        stationId,
        StringComparison.Ordinal);

    private ActionResult<StationAgentControlLeaseApiResponse> ToActionResult(
        Result<StationAgentControlLeaseObservation> result) =>
        result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : Error(result.Error);

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

    private static StationAgentControlLeaseApiResponse ToResponse(
        StationAgentControlLeaseObservation observation)
    {
        var lease = observation.Lease
            ?? throw new InvalidOperationException(
                "A successful control lease response requires a lease.");
        return new StationAgentControlLeaseApiResponse(
            lease.StationId.Value,
            lease.OwnerAgentId,
            lease.OwnerInstanceId,
            lease.FencingToken,
            observation.IsActive,
            lease.AcquiredAtUtc,
            lease.RenewedAtUtc,
            lease.ExpiresAtUtc,
            observation.ObservedAtUtc);
    }

    private ActionResult<StationAgentControlLeaseApiResponse> ToActionResult(
        Result<StationAgentControlLeaseAcquisition> result)
    {
        if (result.IsSuccess)
        {
            return Ok(ToResponse(
                result.Value.Observation));
        }

        return Error(result.Error);
    }

    private string? GetOptionalLeaseHandle()
    {
        var values = Request.Headers[LeaseHandleHeader];
        if (values.Count == 0)
        {
            return null;
        }

        return values.Count == 1
            ? values[0]
            : throw new ArgumentException(
                $"{LeaseHandleHeader} must be supplied exactly once.");
    }

    private string GetRequiredLeaseHandle() =>
        GetOptionalLeaseHandle()
        ?? throw new ArgumentException(
            $"{LeaseHandleHeader} is required.");

    private static ProblemDetails Problem(int status, string title, string detail) =>
        new()
        {
            Status = status,
            Title = title,
            Detail = detail
        };
}
