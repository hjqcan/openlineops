using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.TestPlans;

namespace OpenLineOps.Quality.Domain.Testing;

public sealed class MeasurementResult : Entity<MeasurementResultId>
{
    private MeasurementResult(
        MeasurementResultId id,
        TestAttemptId attemptId,
        TestPlanRevisionId testPlanRevisionId,
        TestCharacteristic characteristic,
        string rawValue,
        decimal normalizedValue,
        CalibrationAsset calibrationAsset,
        string stepVersion,
        string evidenceSha256,
        DateTimeOffset measuredAtUtc)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(characteristic);
        ArgumentNullException.ThrowIfNull(calibrationAsset);

        if (!string.Equals(characteristic.StepVersion, stepVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Step version '{stepVersion}' does not match frozen characteristic step version '{characteristic.StepVersion}'.",
                nameof(stepVersion));
        }

        AttemptId = attemptId;
        TestPlanRevisionId = testPlanRevisionId;
        CharacteristicId = characteristic.Id;
        CharacteristicCode = characteristic.Code;
        RawValue = QualityGuard.RawValue(rawValue, nameof(rawValue));
        NormalizedValue = normalizedValue;
        Unit = characteristic.Unit;
        LimitSetId = characteristic.Limits?.Id;
        LowerLimit = characteristic.Limits?.LowerLimit;
        LowerInclusive = characteristic.Limits?.LowerInclusive;
        UpperLimit = characteristic.Limits?.UpperLimit;
        UpperInclusive = characteristic.Limits?.UpperInclusive;
        InstrumentId = calibrationAsset.InstrumentId;
        CalibrationAssetId = calibrationAsset.Id;
        CalibrationCertificateId = calibrationAsset.CertificateId;
        StepVersion = QualityGuard.CanonicalText(stepVersion, nameof(stepVersion));
        EvidenceSha256 = QualityGuard.Sha256(evidenceSha256, nameof(evidenceSha256));
        MeasuredAtUtc = QualityGuard.Utc(measuredAtUtc, nameof(measuredAtUtc));
        CalibrationStatus = calibrationAsset.GetStatus(MeasuredAtUtc);
        Judgement = Judge(characteristic.Limits, normalizedValue, CalibrationStatus);
    }

    public TestAttemptId AttemptId { get; }

    public TestPlanRevisionId TestPlanRevisionId { get; }

    public TestCharacteristicId CharacteristicId { get; }

    public string CharacteristicCode { get; }

    public string RawValue { get; }

    public decimal NormalizedValue { get; }

    public string Unit { get; }

    public LimitSetId? LimitSetId { get; }

    public decimal? LowerLimit { get; }

    public bool? LowerInclusive { get; }

    public decimal? UpperLimit { get; }

    public bool? UpperInclusive { get; }

    public MeasurementJudgement Judgement { get; }

    public string InstrumentId { get; }

    public CalibrationAssetId CalibrationAssetId { get; }

    public string CalibrationCertificateId { get; }

    public CalibrationStatus CalibrationStatus { get; }

    public string StepVersion { get; }

    public string EvidenceSha256 { get; }

    public DateTimeOffset MeasuredAtUtc { get; }

    public static MeasurementResult Create(
        MeasurementResultId id,
        TestAttemptId attemptId,
        TestPlanRevisionId testPlanRevisionId,
        TestCharacteristic characteristic,
        string rawValue,
        decimal normalizedValue,
        CalibrationAsset calibrationAsset,
        string stepVersion,
        string evidenceSha256,
        DateTimeOffset measuredAtUtc)
    {
        return new MeasurementResult(
            id,
            attemptId,
            testPlanRevisionId,
            characteristic,
            rawValue,
            normalizedValue,
            calibrationAsset,
            stepVersion,
            evidenceSha256,
            measuredAtUtc);
    }

    private static MeasurementJudgement Judge(
        LimitSet? limits,
        decimal normalizedValue,
        CalibrationStatus calibrationStatus)
    {
        if (calibrationStatus != CalibrationStatus.Valid)
        {
            return MeasurementJudgement.Invalid;
        }

        if (limits is null)
        {
            return MeasurementJudgement.NotEvaluated;
        }

        return limits.Contains(normalizedValue)
            ? MeasurementJudgement.Passed
            : MeasurementJudgement.Failed;
    }
}
