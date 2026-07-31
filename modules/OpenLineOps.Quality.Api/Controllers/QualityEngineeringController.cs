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
[Route("api/quality")]
[Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
public sealed class QualityEngineeringController(IQualityService qualityService)
    : ControllerBase
{
    [HttpPost("test-plans")]
    [ProducesResponseType<TestPlanRevisionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TestPlanRevisionResponse>> CreateTestPlanAsync(
        CreateTestPlanRevisionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await qualityService.CreateTestPlanRevisionAsync(
                new CreateTestPlanRevisionCommand(
                    request.TestPlanRevisionId,
                    request.TestPlanId,
                    request.RevisionNumber,
                    request.DisplayName,
                    request.Characteristics.Select(characteristic =>
                            new CreateTestCharacteristicCommand(
                                characteristic.TestCharacteristicId,
                                characteristic.Code,
                                characteristic.DisplayName,
                                characteristic.Unit,
                                characteristic.StepVersion,
                                characteristic.IsRequired,
                                characteristic.Limits is null
                                    ? null
                                    : new CreateLimitSetCommand(
                                        characteristic.Limits.LimitSetId,
                                        characteristic.Limits.Unit,
                                        characteristic.Limits.LowerLimit,
                                        characteristic.Limits.LowerInclusive,
                                        characteristic.Limits.UpperLimit,
                                        characteristic.Limits.UpperInclusive)))
                        .ToArray(),
                    User.GetRequiredActorId(),
                    idempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return QualityApiMapper.ToProblem(result.Error);
        }

        var response = QualityApiMapper.ToResponse(result.Value);
        return Created($"/api/quality/test-plans/{response.TestPlanRevisionId:D}", response);
    }

    [HttpGet("test-plans/{testPlanRevisionId:guid}")]
    [ProducesResponseType<TestPlanRevisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TestPlanRevisionResponse>> GetTestPlanAsync(
        Guid testPlanRevisionId,
        CancellationToken cancellationToken)
    {
        var result = await qualityService
            .GetTestPlanRevisionAsync(testPlanRevisionId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(QualityApiMapper.ToResponse(result.Value));
    }

    [HttpGet("test-plans")]
    [ProducesResponseType<IReadOnlyCollection<TestPlanRevisionResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<TestPlanRevisionResponse>>> ListTestPlansAsync(
        CancellationToken cancellationToken)
    {
        var result = await qualityService
            .ListTestPlanRevisionsAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(result.Value.Select(QualityApiMapper.ToResponse).ToArray());
    }

    [HttpPost("calibration-assets")]
    [ProducesResponseType<CalibrationAssetResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CalibrationAssetResponse>> CreateCalibrationAssetAsync(
        CreateCalibrationAssetRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await qualityService.CreateCalibrationAssetAsync(
                new CreateCalibrationAssetCommand(
                    request.CalibrationAssetId,
                    request.AssetCode,
                    request.InstrumentId,
                    request.CertificateId,
                    request.CalibratedAtUtc,
                    request.ValidUntilUtc,
                    User.GetRequiredActorId(),
                    idempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return QualityApiMapper.ToProblem(result.Error);
        }

        var response = QualityApiMapper.ToResponse(result.Value);
        return Created($"/api/quality/calibration-assets/{response.CalibrationAssetId:D}", response);
    }

    [HttpGet("calibration-assets/{calibrationAssetId:guid}")]
    [ProducesResponseType<CalibrationAssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CalibrationAssetResponse>> GetCalibrationAssetAsync(
        Guid calibrationAssetId,
        CancellationToken cancellationToken)
    {
        var result = await qualityService
            .GetCalibrationAssetAsync(calibrationAssetId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(QualityApiMapper.ToResponse(result.Value));
    }

    [HttpGet("calibration-assets")]
    [ProducesResponseType<IReadOnlyCollection<CalibrationAssetResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<CalibrationAssetResponse>>> ListCalibrationAssetsAsync(
        CancellationToken cancellationToken)
    {
        var result = await qualityService
            .ListCalibrationAssetsAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? QualityApiMapper.ToProblem(result.Error)
            : Ok(result.Value.Select(QualityApiMapper.ToResponse).ToArray());
    }
}
