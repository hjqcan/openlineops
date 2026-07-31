using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Operations.Metrics.Api.Mapping;
using OpenLineOps.Operations.Metrics.Api.Models;
using OpenLineOps.Operations.Metrics.Application.Contracts;
using OpenLineOps.Operations.Metrics.Domain.Production;

namespace OpenLineOps.Operations.Metrics.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Operations)]
[Route("api/operations")]
[Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
public sealed class OperationsMetricsStationAgentController(
    IOperationsMetricsService service) : OperationsMetricsControllerBase
{
    [HttpPost("production-events")]
    [ProducesResponseType<ProductionEventResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProductionEventResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionEventResponse>>
        RecordProductionEventAsync(
            RecordProductionEventRequest request,
            CancellationToken cancellationToken)
    {
        if (!TryGetStationId(out var stationId)
            || !string.Equals(
                stationId,
                request.StationId,
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        try
        {
            var kind = ParseProductionEventKind(request.Kind);
            var result = await service.RecordProductionEventAsync(
                    new RecordProductionEventCommand(
                        request.EventId,
                        stationId,
                        request.UnitId,
                        kind,
                        request.SourceTimestampUtc,
                        request.OccurredAtUtc,
                        request.FirstAttempt,
                        request.Good,
                        request.CycleDurationMilliseconds is null
                            ? null
                            : TimeSpan.FromMilliseconds(
                                request.CycleDurationMilliseconds.Value),
                        request.SchemaVersion),
                    cancellationToken)
                .ConfigureAwait(false);
            var response = OperationsMetricsApiMapper.ToResponse(result.Resource);
            return result.Outcome == OperationsMetricsWriteOutcome.Applied
                ? Created(
                    $"/api/operations/production-events/{Uri.EscapeDataString(response.EventId)}",
                    response)
                : Ok(response);
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
    }

    [HttpPost("downtime")]
    [ProducesResponseType<DowntimeResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<DowntimeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DowntimeResponse>> OpenDowntimeAsync(
        OpenDowntimeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId)
            || !TryGetStationId(out var stationId)
            || !string.Equals(
                stationId,
                request.StationId,
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        try
        {
            var result = await service.OpenDowntimeAsync(
                    new OpenDowntimeCommand(
                        request.FactId,
                        request.DowntimeId,
                        stationId,
                        request.SourceId,
                        request.OccurredAtUtc,
                        actorId),
                    cancellationToken)
                .ConfigureAwait(false);
            var response = OperationsMetricsApiMapper.ToResponse(result.Resource);
            return result.Outcome == OperationsMetricsWriteOutcome.Applied
                ? Created(
                    $"/api/operations/downtime/{Uri.EscapeDataString(response.DowntimeId)}",
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

    [HttpPost("downtime/{downtimeId}/source-clearance")]
    [ProducesResponseType<DowntimeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DowntimeResponse>> ClearDowntimeAsync(
        string downtimeId,
        ClearDowntimeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId)
            || !TryGetStationId(out var stationId)
            || !string.Equals(
                stationId,
                request.StationId,
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        try
        {
            var result = await service.ClearDowntimeFromSourceAsync(
                    new ClearDowntimeFromSourceCommand(
                        request.FactId,
                        downtimeId,
                        stationId,
                        request.SourceId,
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
        catch (UnauthorizedAccessException)
        {
            return Forbid();
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

    private static ProductionEventKind ParseProductionEventKind(string value)
    {
        return Enum.TryParse<ProductionEventKind>(
                   value,
                   ignoreCase: false,
                   out var parsed)
               && Enum.IsDefined(parsed)
               && string.Equals(value, parsed.ToString(), StringComparison.Ordinal)
            ? parsed
            : throw new ArgumentException(
                $"Production event kind '{value}' is invalid.",
                nameof(value));
    }
}
