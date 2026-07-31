using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Operations.Metrics.Api.Mapping;
using OpenLineOps.Operations.Metrics.Api.Models;
using OpenLineOps.Operations.Metrics.Application.Contracts;

namespace OpenLineOps.Operations.Metrics.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Operations)]
[Route("api/operations/shifts")]
[Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
public sealed class OperationsMetricsEngineeringController(
    IOperationsMetricsService service) : OperationsMetricsControllerBase
{
    [HttpPost]
    [ProducesResponseType<ShiftDefinitionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ShiftDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShiftDefinitionResponse>> DefineShiftAsync(
        DefineShiftRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Forbid();
        }

        try
        {
            var result = await service.DefineShiftAsync(
                    new DefineShiftCommand(
                        request.ShiftId,
                        request.StationId,
                        request.Name,
                        request.TimeZoneId,
                        request.LocalStartTime,
                        request.LocalEndTime,
                        actorId,
                        request.SchemaVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            var response = OperationsMetricsApiMapper.ToResponse(result.Resource);
            return result.Outcome == OperationsMetricsWriteOutcome.Applied
                ? Created(
                    $"/api/operations/shifts/{Uri.EscapeDataString(response.ShiftId)}",
                    response)
                : Ok(response);
        }
        catch (OperationsMetricsConflictException exception)
        {
            return ConflictProblemResult(exception);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
    }

    [HttpPost("{shiftId}/windows")]
    [ProducesResponseType<PlannedProductionWindowResponse>(
        StatusCodes.Status201Created)]
    [ProducesResponseType<PlannedProductionWindowResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PlannedProductionWindowResponse>>
        ScheduleWindowAsync(
            string shiftId,
            ScheduleProductionWindowRequest request,
            CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Forbid();
        }

        try
        {
            var result = await service.ScheduleProductionWindowAsync(
                    new ScheduleProductionWindowCommand(
                        request.WindowId,
                        shiftId,
                        request.StationId,
                        request.StartsAtUtc,
                        request.EndsAtUtc,
                        request.TargetQuantity,
                        TimeSpan.FromMilliseconds(
                            request.IdealCycleTimeMilliseconds),
                        actorId,
                        request.SchemaVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            var response = OperationsMetricsApiMapper.ToResponse(result.Resource);
            return result.Outcome == OperationsMetricsWriteOutcome.Applied
                ? Created(
                    $"/api/operations/shifts/{Uri.EscapeDataString(shiftId)}/windows/{Uri.EscapeDataString(response.WindowId)}",
                    response)
                : Ok(response);
        }
        catch (KeyNotFoundException)
        {
            return NotFoundProblemResult("Shift", shiftId);
        }
        catch (OperationsMetricsConflictException exception)
        {
            return ConflictProblemResult(exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            return ValidationProblemResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ConflictProblemResult(exception);
        }
    }
}
