using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Quality.Api.Mapping;
using OpenLineOps.Quality.Api.Models;
using OpenLineOps.Quality.Application.Contracts;
using OpenLineOps.Quality.Application.Services;

namespace OpenLineOps.Quality.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Quality)]
[Route("api/quality/attempts")]
[Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
public sealed class QualityStationAgentController(IQualityService qualityService)
    : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<TestAttemptResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TestAttemptResponse>> CreateAttemptAsync(
        CreateTestAttemptRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await qualityService.CreateTestAttemptAsync(
                new CreateTestAttemptCommand(
                    request.TestAttemptId,
                    request.TestPlanRevisionId,
                    request.ProductionUnitId,
                    request.AttemptNumber,
                    request.StartedAtUtc,
                    User.GetRequiredStationId(),
                    User.GetRequiredActorId(),
                    idempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return QualityApiMapper.ToProblem(result.Error);
        }

        var response = QualityApiMapper.ToResponse(result.Value);
        return Created($"/api/quality/attempts/{response.TestAttemptId:D}", response);
    }

    [HttpPost("{testAttemptId:guid}/measurements")]
    [ProducesResponseType<TestAttemptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TestAttemptResponse>> AppendMeasurementAsync(
        Guid testAttemptId,
        AppendMeasurementRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await qualityService.AppendMeasurementAsync(
                new AppendMeasurementCommand(
                    testAttemptId,
                    request.ExpectedRevision,
                    request.MeasurementResultId,
                    request.TestCharacteristicId,
                    request.RawValue,
                    request.NormalizedValue,
                    request.CalibrationAssetId,
                    request.StepVersion,
                    request.EvidenceSha256,
                    request.MeasuredAtUtc,
                    User.GetRequiredStationId(),
                    User.GetRequiredActorId(),
                    idempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(QualityApiMapper.ToResponse(result.Value));
    }

    [HttpPost("{testAttemptId:guid}/completion")]
    [ProducesResponseType<TestAttemptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TestAttemptResponse>> CompleteAttemptAsync(
        Guid testAttemptId,
        CompleteTestAttemptRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await qualityService.CompleteTestAttemptAsync(
                new CompleteTestAttemptCommand(
                    testAttemptId,
                    request.ExpectedRevision,
                    request.CompletedAtUtc,
                    User.GetRequiredStationId(),
                    User.GetRequiredActorId(),
                    idempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(QualityApiMapper.ToResponse(result.Value));
    }
}
