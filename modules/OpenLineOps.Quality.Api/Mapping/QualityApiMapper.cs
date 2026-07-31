using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Quality.Api.Models;
using OpenLineOps.Quality.Application.Contracts;

namespace OpenLineOps.Quality.Api.Mapping;

internal static class QualityApiMapper
{
    public static TestPlanRevisionResponse ToResponse(TestPlanRevisionDetails details) =>
        new(
            details.TestPlanRevisionId,
            details.TestPlanId,
            details.RevisionNumber,
            details.ResourceRevision,
            details.DisplayName,
            details.CreatedAtUtc,
            details.CreatedBy,
            details.Characteristics.Select(ToResponse).ToArray());

    public static CalibrationAssetResponse ToResponse(CalibrationAssetDetails details) =>
        new(
            details.CalibrationAssetId,
            details.ResourceRevision,
            details.AssetCode,
            details.InstrumentId,
            details.CertificateId,
            details.CalibratedAtUtc,
            details.ValidUntilUtc,
            details.Status.ToString(),
            details.CreatedBy);

    public static TestAttemptResponse ToResponse(TestAttemptDetails details) =>
        new(
            details.TestAttemptId,
            details.TestPlanRevisionId,
            details.ResourceRevision,
            details.ProductionUnitId,
            details.StationId,
            details.AttemptNumber,
            details.StartedAtUtc,
            details.CompletedAtUtc,
            details.Status.ToString(),
            details.Judgement.ToString(),
            details.AbortReason,
            details.CreatedBy,
            details.Measurements.Select(ToResponse).ToArray());

    public static NonconformanceResponse ToResponse(NonconformanceDetails details) =>
        new(
            details.NonconformanceId,
            details.ResourceRevision,
            details.TestAttemptId,
            details.MeasurementResultId,
            details.TestPlanRevisionId,
            details.TestCharacteristicId,
            details.ProductionUnitId,
            details.StationId,
            details.Code,
            details.Description,
            details.Severity.ToString(),
            details.Status.ToString(),
            details.RaisedAtUtc,
            details.Disposition?.ToString(),
            details.DispositionedBy,
            details.DispositionReason,
            details.DispositionedAtUtc,
            details.CreatedBy);

    public static ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };
        return new ObjectResult(new ProblemDetails
        {
            Status = statusCode,
            Title = error.Code,
            Detail = error.Message
        })
        {
            StatusCode = statusCode
        };
    }

    private static TestCharacteristicResponse ToResponse(TestCharacteristicDetails details) =>
        new(
            details.TestCharacteristicId,
            details.Code,
            details.DisplayName,
            details.Unit,
            details.StepVersion,
            details.IsRequired,
            details.Limits is null
                ? null
                : new LimitSetResponse(
                    details.Limits.LimitSetId,
                    details.Limits.Unit,
                    details.Limits.LowerLimit,
                    details.Limits.LowerInclusive,
                    details.Limits.UpperLimit,
                    details.Limits.UpperInclusive));

    private static MeasurementResultResponse ToResponse(MeasurementResultDetails details) =>
        new(
            details.MeasurementResultId,
            details.TestAttemptId,
            details.TestPlanRevisionId,
            details.TestCharacteristicId,
            details.CharacteristicCode,
            details.RawValue,
            details.NormalizedValue,
            details.Unit,
            details.LimitSetId,
            details.LowerLimit,
            details.LowerInclusive,
            details.UpperLimit,
            details.UpperInclusive,
            details.Judgement.ToString(),
            details.InstrumentId,
            details.CalibrationAssetId,
            details.CalibrationCertificateId,
            details.CalibrationStatus.ToString(),
            details.StepVersion,
            details.EvidenceSha256,
            details.MeasuredAtUtc);
}
