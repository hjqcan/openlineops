using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.Testing;

namespace OpenLineOps.Quality.Tests;

public sealed class TestAttemptTests
{
    [Fact]
    public void MeasurementFreezesQualityEvidenceAndPassesWithinLimits()
    {
        var characteristic = QualityTestData.CreateCharacteristic();
        var plan = QualityTestData.CreatePlan(characteristic);
        var calibration = QualityTestData.CreateCalibration();
        var attempt = QualityTestData.StartAttempt(plan);

        var measurement = QualityTestData.Record(
            attempt,
            characteristic,
            normalizedValue: 5.125m,
            calibration,
            rawValue: "5.1250 V");

        Assert.Equal(attempt.Id, measurement.AttemptId);
        Assert.Equal(plan.Id, measurement.TestPlanRevisionId);
        Assert.Equal(characteristic.Id, measurement.CharacteristicId);
        Assert.Equal("5.1250 V", measurement.RawValue);
        Assert.Equal(5.125m, measurement.NormalizedValue);
        Assert.Equal("V", measurement.Unit);
        Assert.Equal(4.5m, measurement.LowerLimit);
        Assert.Equal(true, measurement.LowerInclusive);
        Assert.Equal(5.5m, measurement.UpperLimit);
        Assert.Equal(true, measurement.UpperInclusive);
        Assert.Equal(MeasurementJudgement.Passed, measurement.Judgement);
        Assert.Equal(calibration.InstrumentId, measurement.InstrumentId);
        Assert.Equal(calibration.Id, measurement.CalibrationAssetId);
        Assert.Equal(calibration.CertificateId, measurement.CalibrationCertificateId);
        Assert.Equal(CalibrationStatus.Valid, measurement.CalibrationStatus);
        Assert.Equal(characteristic.StepVersion, measurement.StepVersion);
        Assert.Equal(QualityTestData.EvidenceSha256, measurement.EvidenceSha256);
    }

    [Fact]
    public void MeasurementFailsOutsideLimitsAndIsInvalidWithExpiredCalibration()
    {
        var first = QualityTestData.CreateCharacteristic();
        var second = QualityTestData.CreateCharacteristic(
            code: "supply.current",
            displayName: "Supply current",
            unit: "A",
            stepVersion: "step-current@1");
        var attempt = QualityTestData.StartAttempt(QualityTestData.CreatePlan(first, second));
        var expiredCalibration = QualityTestData.CreateCalibration(
            validUntilUtc: QualityTestData.BaseTimeUtc);

        var failed = QualityTestData.Record(attempt, first, normalizedValue: 6m);
        var invalid = QualityTestData.Record(
            attempt,
            second,
            normalizedValue: 5m,
            expiredCalibration);

        Assert.Equal(MeasurementJudgement.Failed, failed.Judgement);
        Assert.Equal(CalibrationStatus.Expired, invalid.CalibrationStatus);
        Assert.Equal(MeasurementJudgement.Invalid, invalid.Judgement);
    }

    [Fact]
    public void OptionalCharacteristicWithoutLimitsIsNotEvaluated()
    {
        var required = QualityTestData.CreateCharacteristic();
        var informational = QualityTestData.CreateCharacteristic(
            code: "ambient.temperature",
            displayName: "Ambient temperature",
            isRequired: false,
            unit: "Cel");
        var attempt = QualityTestData.StartAttempt(
            QualityTestData.CreatePlan(required, informational));

        QualityTestData.Record(attempt, required, normalizedValue: 5m);
        var info = QualityTestData.Record(attempt, informational, normalizedValue: 23.4m);
        attempt.Complete(QualityTestData.BaseTimeUtc.AddSeconds(2));

        Assert.Equal(MeasurementJudgement.NotEvaluated, info.Judgement);
        Assert.Equal(TestAttemptJudgement.Passed, attempt.Judgement);
    }

    [Fact]
    public void CompletionRequiresEveryRequiredCharacteristic()
    {
        var first = QualityTestData.CreateCharacteristic();
        var second = QualityTestData.CreateCharacteristic(
            code: "supply.current",
            displayName: "Supply current",
            unit: "A",
            stepVersion: "step-current@1");
        var attempt = QualityTestData.StartAttempt(QualityTestData.CreatePlan(first, second));
        QualityTestData.Record(attempt, first, normalizedValue: 5m);

        Assert.Throws<InvalidOperationException>(
            () => attempt.Complete(QualityTestData.BaseTimeUtc.AddSeconds(2)));
    }

    [Fact]
    public void CompletionDerivesFailedAndInvalidAttemptJudgements()
    {
        var failedCharacteristic = QualityTestData.CreateCharacteristic();
        var failedAttempt = QualityTestData.StartAttempt(
            QualityTestData.CreatePlan(failedCharacteristic),
            unitId: "unit-failed");
        QualityTestData.Record(failedAttempt, failedCharacteristic, normalizedValue: 6m);
        failedAttempt.Complete(QualityTestData.BaseTimeUtc.AddSeconds(2));

        var invalidCharacteristic = QualityTestData.CreateCharacteristic();
        var invalidAttempt = QualityTestData.StartAttempt(
            QualityTestData.CreatePlan(invalidCharacteristic),
            unitId: "unit-invalid");
        QualityTestData.Record(
            invalidAttempt,
            invalidCharacteristic,
            normalizedValue: 5m,
            QualityTestData.CreateCalibration(validUntilUtc: QualityTestData.BaseTimeUtc));
        invalidAttempt.Complete(QualityTestData.BaseTimeUtc.AddSeconds(2));

        Assert.Equal(TestAttemptJudgement.Failed, failedAttempt.Judgement);
        Assert.Equal(TestAttemptJudgement.Invalid, invalidAttempt.Judgement);
    }

    [Fact]
    public void AttemptRejectsDuplicateCharacteristicsAndUnknownPlanEntries()
    {
        var characteristic = QualityTestData.CreateCharacteristic();
        var attempt = QualityTestData.StartAttempt(QualityTestData.CreatePlan(characteristic));
        QualityTestData.Record(attempt, characteristic, normalizedValue: 5m);

        Assert.Throws<InvalidOperationException>(
            () => QualityTestData.Record(attempt, characteristic, normalizedValue: 5.1m));
        Assert.Throws<KeyNotFoundException>(
            () => attempt.RecordMeasurement(
                MeasurementResultId.New(),
                TestCharacteristicId.New(),
                "5.0",
                5m,
                QualityTestData.CreateCalibration(),
                characteristic.StepVersion,
                QualityTestData.EvidenceSha256,
                QualityTestData.BaseTimeUtc.AddSeconds(2)));
    }

    [Fact]
    public void MeasurementRejectsWrongStepVersionAndMalformedEvidenceHash()
    {
        var characteristic = QualityTestData.CreateCharacteristic();
        var plan = QualityTestData.CreatePlan(characteristic);
        var attempt = QualityTestData.StartAttempt(plan);

        Assert.Throws<ArgumentException>(
            () => attempt.RecordMeasurement(
                MeasurementResultId.New(),
                characteristic.Id,
                "5.0",
                5m,
                QualityTestData.CreateCalibration(),
                "step-voltage@2",
                QualityTestData.EvidenceSha256,
                QualityTestData.BaseTimeUtc.AddSeconds(1)));
        Assert.Throws<ArgumentException>(
            () => attempt.RecordMeasurement(
                MeasurementResultId.New(),
                characteristic.Id,
                "5.0",
                5m,
                QualityTestData.CreateCalibration(),
                characteristic.StepVersion,
                "not-a-sha256",
                QualityTestData.BaseTimeUtc.AddSeconds(1)));
        Assert.Empty(attempt.Measurements);
    }

    [Fact]
    public void CompletedOrAbortedAttemptsCannotAcceptMoreMeasurements()
    {
        var completedCharacteristic = QualityTestData.CreateCharacteristic();
        var completed = QualityTestData.StartAttempt(
            QualityTestData.CreatePlan(completedCharacteristic),
            "unit-completed");
        QualityTestData.Record(completed, completedCharacteristic, normalizedValue: 5m);
        completed.Complete(QualityTestData.BaseTimeUtc.AddSeconds(2));

        var abortedCharacteristic = QualityTestData.CreateCharacteristic();
        var aborted = QualityTestData.StartAttempt(
            QualityTestData.CreatePlan(abortedCharacteristic),
            "unit-aborted");
        aborted.Abort("Instrument communication failed.", QualityTestData.BaseTimeUtc.AddSeconds(1));

        Assert.Equal(TestAttemptStatus.Completed, completed.Status);
        Assert.Throws<InvalidOperationException>(
            () => QualityTestData.Record(completed, completedCharacteristic, normalizedValue: 5m));
        Assert.Equal(TestAttemptStatus.Aborted, aborted.Status);
        Assert.Equal(TestAttemptJudgement.Invalid, aborted.Judgement);
        Assert.Equal("Instrument communication failed.", aborted.AbortReason);
        Assert.Throws<InvalidOperationException>(
            () => QualityTestData.Record(aborted, abortedCharacteristic, normalizedValue: 5m));
    }
}
