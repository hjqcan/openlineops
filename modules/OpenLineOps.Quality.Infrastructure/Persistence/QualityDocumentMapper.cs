using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.Testing;
using OpenLineOps.Quality.Domain.TestPlans;

namespace OpenLineOps.Quality.Infrastructure.Persistence;

internal static class QualityDocumentMapper
{
    public static PersistedTestPlanRevision ToDocument(TestPlanRevision revision)
    {
        return new PersistedTestPlanRevision(
            revision.Id.Value,
            revision.TestPlanId.Value,
            revision.RevisionNumber,
            revision.DisplayName,
            revision.CreatedAtUtc,
            revision.Characteristics.Select(ToDocument).ToArray());
    }

    public static TestPlanRevision ToDomain(PersistedTestPlanRevision document)
    {
        return TestPlanRevision.Create(
            new TestPlanRevisionId(document.TestPlanRevisionId),
            new TestPlanId(document.TestPlanId),
            document.RevisionNumber,
            document.DisplayName,
            document.Characteristics.Select(ToDomain).ToArray(),
            document.CreatedAtUtc);
    }

    public static PersistedCalibrationAsset ToDocument(CalibrationAsset asset)
    {
        return new PersistedCalibrationAsset(
            asset.Id.Value,
            asset.AssetCode,
            asset.InstrumentId,
            asset.CertificateId,
            asset.CalibratedAtUtc,
            asset.ValidUntilUtc);
    }

    public static CalibrationAsset ToDomain(PersistedCalibrationAsset document)
    {
        return CalibrationAsset.Create(
            new CalibrationAssetId(document.CalibrationAssetId),
            document.AssetCode,
            document.InstrumentId,
            document.CertificateId,
            document.CalibratedAtUtc,
            document.ValidUntilUtc);
    }

    public static PersistedMeasurementResult ToDocument(MeasurementResult measurement)
    {
        return new PersistedMeasurementResult(
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

    private static PersistedTestCharacteristic ToDocument(TestCharacteristic characteristic)
    {
        return new PersistedTestCharacteristic(
            characteristic.Id.Value,
            characteristic.Code,
            characteristic.DisplayName,
            characteristic.Unit,
            characteristic.StepVersion,
            characteristic.IsRequired,
            characteristic.Limits is null ? null : ToDocument(characteristic.Limits));
    }

    private static PersistedLimitSet ToDocument(LimitSet limits)
    {
        return new PersistedLimitSet(
            limits.Id.Value,
            limits.Unit,
            limits.LowerLimit,
            limits.LowerInclusive,
            limits.UpperLimit,
            limits.UpperInclusive);
    }

    private static TestCharacteristic ToDomain(PersistedTestCharacteristic document)
    {
        return TestCharacteristic.Create(
            new TestCharacteristicId(document.TestCharacteristicId),
            document.Code,
            document.DisplayName,
            document.Unit,
            document.StepVersion,
            document.IsRequired,
            document.Limits is null ? null : ToDomain(document.Limits));
    }

    private static LimitSet ToDomain(PersistedLimitSet document)
    {
        return LimitSet.Create(
            new LimitSetId(document.LimitSetId),
            document.Unit,
            document.LowerLimit,
            document.LowerInclusive,
            document.UpperLimit,
            document.UpperInclusive);
    }
}
