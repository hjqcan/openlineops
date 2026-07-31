using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Integration.Api.Mapping;
using OpenLineOps.Integration.Api.Transport;
using OpenLineOps.Integration.Application.Inbox;

namespace OpenLineOps.Integration.Api.Controllers;

public abstract class IntegrationControllerBase : ControllerBase
{
    protected bool TryGetActorId(out string actorId)
    {
        actorId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        return User.Identity?.IsAuthenticated == true
            && !string.IsNullOrWhiteSpace(actorId);
    }

    protected bool TryGetStationId(out string stationId)
    {
        stationId = User.FindFirstValue(OpenLineOpsApiSecurity.StationIdClaim)
            ?? string.Empty;
        return User.Identity?.IsAuthenticated == true
            && !string.IsNullOrWhiteSpace(stationId);
    }

    protected ObjectResult ValidationProblemResult(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return BadRequest(IntegrationApiMapper.Problem(
            StatusCodes.Status400BadRequest,
            "Validation.Integration.Request",
            exception.Message));
    }

    protected ObjectResult ConflictProblemResult(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Conflict(IntegrationApiMapper.Problem(
            StatusCodes.Status409Conflict,
            exception is IntegrationMessageConflictException
                ? "Integration.IdempotencyConflict"
                : "Integration.StateConflict",
            exception.Message));
    }

    protected ObjectResult NotFoundProblemResult(string resource, string id)
    {
        return NotFound(IntegrationApiMapper.Problem(
            StatusCodes.Status404NotFound,
            $"Integration.{resource}NotFound",
            $"{resource} '{id}' was not found."));
    }

    protected ObjectResult UnavailableProblemResult(
        IntegrationEndpointUnavailableException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            IntegrationApiMapper.Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Integration.EndpointUnavailable",
                exception.Message));
    }
}
