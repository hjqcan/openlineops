using OpenLineOps.Quality.Application.Contracts;
using OpenLineOps.Quality.Application.Persistence;
using OpenLineOps.Quality.Domain.TestPlans;
using OpenLineOps.Quality.Domain.Testing;

namespace OpenLineOps.Quality.Application.Services;

internal static class QualityDetailsMapper
{
    public static TestPlanRevisionDetails ToDetails(VersionedTestPlanRevision stored)
    {
        var revision = stored.Value;
        return new TestPlanRevisionDetails(
            revision.Id.Value,
            revision.TestPlanId.Value,
            revision.RevisionNumber,
            stored.Revision,
            revision.DisplayName,
            revision.CreatedAtUtc,
            stored.CreatedBy,
            revision.Characteristics.Select(ToDetails).ToArray());
    }

    public static CalibrationAssetDetails ToDetails(
        VersionedCalibrationAsset stored,
        DateTimeOffset atUtc)
    {
        var asset = stored.Value;
        return new CalibrationAssetDetails(
            asset.Id.Value,
            stored.Revision,
            asset.AssetCode,
            asset.InstrumentId,
            asset.CertificateId,
            asset.CalibratedAtUtc,
            asset.ValidUntilUtc,
            asset.GetStatus(atUtc),
            stored.CreatedBy);
    }

    public static TestAttemptDetails ToDetails(VersionedTestAttempt stored)
    {
        var attempt = stored.Value;
        return new TestAttemptDetails(
            attempt.Id.Value,
            attempt.TestPlanRevisionId.Value,
            stored.Revision,
            attempt.ProductionUnitId,
            attempt.StationId,
            attempt.AttemptNumber,
            attempt.StartedAtUtc,
            attempt.CompletedAtUtc,
            attempt.Status,
            attempt.Judgement,
            attempt.AbortReason,
            stored.CreatedBy,
            attempt.Measurements.Select(ToDetails).ToArray());
    }

    public static NonconformanceDetails ToDetails(VersionedNonconformance stored)
    {
        var nonconformance = stored.Value;
        return new NonconformanceDetails(
            nonconformance.Id.Value,
            stored.Revision,
            nonconformance.TestAttemptId.Value,
            nonconformance.MeasurementResultId.Value,
            nonconformance.TestPlanRevisionId.Value,
            nonconformance.TestCharacteristicId.Value,
            nonconformance.ProductionUnitId,
            stored.StationId,
            nonconformance.Code,
            nonconformance.Description,
            nonconformance.Severity,
            nonconformance.Status,
            nonconformance.RaisedAtUtc,
            nonconformance.Disposition,
            nonconformance.DispositionedBy,
            nonconformance.DispositionReason,
            nonconformance.DispositionedAtUtc,
            stored.CreatedBy);
    }

    private static TestCharacteristicDetails ToDetails(TestCharacteristic characteristic)
    {
        return new TestCharacteristicDetails(
            characteristic.Id.Value,
            characteristic.Code,
            characteristic.DisplayName,
            characteristic.Unit,
            characteristic.StepVersion,
            characteristic.IsRequired,
            characteristic.Limits is null ? null : ToDetails(characteristic.Limits));
    }

    private static LimitSetDetails ToDetails(LimitSet limits)
    {
        return new LimitSetDetails(
            limits.Id.Value,
            limits.Unit,
            limits.LowerLimit,
            limits.LowerInclusive,
            limits.UpperLimit,
            limits.UpperInclusive);
    }

    private static MeasurementResultDetails ToDetails(MeasurementResult measurement)
    {
        return new MeasurementResultDetails(
            measurement.Id.Value,
            measurement.AttemptId.Value,
            measurement.TestPlanRevisionId.Value,
            measurement.CharacteristicId.Value,
            measurement.CharacteristicCode,
            measurement.RawValue,
            measurement.NormalizedValue,
            measurement.Unit,
            measurement.LimitSetId?.Value,
            measurement.LowerLimit,
            measurement.LowerInclusive,
            measurement.UpperLimit,
            measurement.UpperInclusive,
            measurement.Judgement,
            measurement.InstrumentId,
            measurement.CalibrationAssetId.Value,
            measurement.CalibrationCertificateId,
            measurement.CalibrationStatus,
            measurement.StepVersion,
            measurement.EvidenceSha256,
            measurement.MeasuredAtUtc);
    }
}
