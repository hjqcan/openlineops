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
[Route("api/operations")]
[Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class OperationsMetricsOperatorController(
    IOperationsMetricsService service) : OperationsMetricsControllerBase
{
    [HttpPost("downtime/{downtimeId}/reason")]
    [ProducesResponseType<DowntimeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DowntimeResponse>> AttributeReasonAsync(
        string downtimeId,
        AttributeDowntimeReasonRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Forbid();
        }

        try
        {
            var result = await service.AttributeDowntimeReasonAsync(
                    new AttributeDowntimeReasonCommand(
                        request.FactId,
                        downtimeId,
                        request.ReasonCode,
                        request.ReasonComment,
                        request.OccurredAtUtc,
                        actorId,
                        request.ExpectedRevision),
                    cancellationToken)
                .ConfigureAwait(false);
            return Ok(OperationsMetricsApiMapper.ToResponse(result.Resource));
        }
        catch (KeyNotFoundException)
        {
            return NotFoundProblemResult("Downtime", downtimeId);
        }
        catch (OperationsMetricsConflictException exception)
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

    [HttpGet("downtime")]
    [ProducesResponseType<IReadOnlyList<DowntimeResponse>>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<DowntimeResponse>>>
        QueryDowntimeAsync(
            [FromQuery] string stationId,
            [FromQuery] DateTimeOffset fromUtc,
            [FromQuery] DateTimeOffset toUtc,
            CancellationToken cancellationToken)
    {
        try
        {
            var values = await service.QueryDowntimeAsync(
                    stationId,
                    fromUtc,
                    toUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return Ok(values.Select(OperationsMetricsApiMapper.ToResponse).ToArray());
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
    }

    [HttpGet("oee")]
    [ProducesResponseType<OeeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OeeResponse>> GetOeeAsync(
        [FromQuery] string stationId,
        [FromQuery] DateTimeOffset fromUtc,
        [FromQuery] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = await service.CalculateOeeAsync(
                    new OeeQuery(stationId, fromUtc, toUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            return Ok(OperationsMetricsApiMapper.ToResponse(report));
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
    }

    [HttpGet("shifts")]
    [ProducesResponseType<IReadOnlyList<ShiftScheduleResponse>>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ShiftScheduleResponse>>>
        QueryShiftsAsync(
            [FromQuery] string stationId,
            [FromQuery] DateTimeOffset fromUtc,
            [FromQuery] DateTimeOffset toUtc,
            CancellationToken cancellationToken)
    {
        try
        {
            var schedules = await service.QueryShiftsAsync(
                    stationId,
                    fromUtc,
                    toUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return Ok(schedules.Select(static item => new ShiftScheduleResponse(
                    OperationsMetricsApiMapper.ToResponse(item.Definition),
                    item.Windows
                        .Select(OperationsMetricsApiMapper.ToResponse)
                        .ToArray()))
                .ToArray());
        }
        catch (ArgumentException exception)
        {
            return ValidationProblemResult(exception);
        }
    }
}
