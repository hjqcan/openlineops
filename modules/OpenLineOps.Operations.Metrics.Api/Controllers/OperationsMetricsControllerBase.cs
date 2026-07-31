using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Operations.Metrics.Api.Mapping;
using OpenLineOps.Operations.Metrics.Application.Contracts;

namespace OpenLineOps.Operations.Metrics.Api.Controllers;

public abstract class OperationsMetricsControllerBase : ControllerBase
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

    protected ObjectResult ValidationProblemResult(Exception exception) =>
        BadRequest(OperationsMetricsApiMapper.Problem(
            StatusCodes.Status400BadRequest,
            "Operations.Metrics.Validation",
            exception.Message));

    protected ObjectResult ConflictProblemResult(Exception exception) =>
        Conflict(OperationsMetricsApiMapper.Problem(
            StatusCodes.Status409Conflict,
            exception is OperationsMetricsConflictException
                ? "Operations.Metrics.IdempotencyOrRevisionConflict"
                : "Operations.Metrics.StateConflict",
            exception.Message));

    protected ObjectResult NotFoundProblemResult(
        string resource,
        string id) =>
        NotFound(OperationsMetricsApiMapper.Problem(
            StatusCodes.Status404NotFound,
            $"Operations.Metrics.{resource}NotFound",
            $"{resource} '{id}' was not found."));
}
