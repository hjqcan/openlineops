using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Quality.Api.Mapping;
using OpenLineOps.Quality.Api.Models;
using OpenLineOps.Quality.Application.Contracts;
using OpenLineOps.Quality.Application.Services;
using OpenLineOps.Quality.Domain.Nonconformances;

namespace OpenLineOps.Quality.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = "quality")]
[Route("api/quality")]
[Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class QualityOperationsController(IQualityService qualityService)
    : ControllerBase
{
    [HttpGet("attempts/{testAttemptId:guid}")]
    [ProducesResponseType<TestAttemptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TestAttemptResponse>> GetAttemptAsync(
        Guid testAttemptId,
        CancellationToken cancellationToken)
    {
        var result = await qualityService
            .GetTestAttemptAsync(testAttemptId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(QualityApiMapper.ToResponse(result.Value));
    }

    [HttpGet("attempts")]
    [ProducesResponseType<IReadOnlyCollection<TestAttemptResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<TestAttemptResponse>>> ListAttemptsAsync(
        [FromQuery] string? stationId,
        [FromQuery] string? productionUnitId,
        CancellationToken cancellationToken)
    {
        var result = await qualityService
            .ListTestAttemptsAsync(stationId, productionUnitId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(result.Value.Select(QualityApiMapper.ToResponse).ToArray());
    }

    [HttpGet("nonconformances/{nonconformanceId:guid}")]
    [ProducesResponseType<NonconformanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NonconformanceResponse>> GetNonconformanceAsync(
        Guid nonconformanceId,
        CancellationToken cancellationToken)
    {
        var result = await qualityService
            .GetNonconformanceAsync(nonconformanceId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(QualityApiMapper.ToResponse(result.Value));
    }

    [HttpGet("nonconformances")]
    [ProducesResponseType<IReadOnlyCollection<NonconformanceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyCollection<NonconformanceResponse>>> ListNonconformancesAsync(
        [FromQuery] string? stationId,
        [FromQuery] string? productionUnitId,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptional(status, out NonconformanceStatus? parsedStatus))
        {
            return QualityApiMapper.ToProblem(ApplicationError.Validation(
                "Quality.Nonconformance.Status",
                "Status must be exactly 'Open' or 'Dispositioned'."));
        }

        var result = await qualityService
            .ListNonconformancesAsync(stationId, productionUnitId, parsedStatus, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(result.Value.Select(QualityApiMapper.ToResponse).ToArray());
    }

    [HttpPost("nonconformances/{nonconformanceId:guid}/disposition")]
    [ProducesResponseType<NonconformanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<NonconformanceResponse>> DispositionNonconformanceAsync(
        Guid nonconformanceId,
        DispositionNonconformanceRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<NonconformanceDisposition>(
                request.Disposition,
                ignoreCase: false,
                out var disposition)
            || !Enum.IsDefined(disposition))
        {
            return QualityApiMapper.ToProblem(ApplicationError.Validation(
                "Quality.Nonconformance.Disposition",
                "Disposition must be exactly 'Rework', 'UseAsIs', or 'Scrap'."));
        }

        var result = await qualityService.DispositionNonconformanceAsync(
                new DispositionNonconformanceCommand(
                    nonconformanceId,
                    request.ExpectedRevision,
                    disposition,
                    request.Reason,
                    User.GetRequiredActorId(),
                    idempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(QualityApiMapper.ToResponse(result.Value));
    }

    private static bool TryParseOptional<TEnum>(string? value, out TEnum? parsed)
        where TEnum : struct, Enum
    {
        if (value is null)
        {
            parsed = null;
            return true;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: false, out var candidate)
            && Enum.IsDefined(candidate))
        {
            parsed = candidate;
            return true;
        }

        parsed = null;
        return false;
    }
}
