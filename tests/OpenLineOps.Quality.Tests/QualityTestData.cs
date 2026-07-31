using System.Globalization;
using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.Testing;
using OpenLineOps.Quality.Domain.TestPlans;

namespace OpenLineOps.Quality.Tests;

internal static class QualityTestData
{
    public static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public const string EvidenceSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static LimitSet CreateLimits(
        decimal? lowerLimit = 4.5m,
        decimal? upperLimit = 5.5m,
        bool lowerInclusive = true,
        bool upperInclusive = true,
        string unit = "V")
    {
        return LimitSet.Create(
            LimitSetId.New(),
            unit,
            lowerLimit,
            lowerInclusive,
            upperLimit,
            upperInclusive);
    }

    public static TestCharacteristic CreateCharacteristic(
        string code = "supply.voltage",
        string displayName = "Supply voltage",
        bool isRequired = true,
        LimitSet? limits = null,
        string unit = "V",
        string stepVersion = "step-voltage@1")
    {
        return TestCharacteristic.Create(
            TestCharacteristicId.New(),
            code,
            displayName,
            unit,
            stepVersion,
            isRequired,
            limits ?? (isRequired ? CreateLimits(unit: unit) : null));
    }

    public static TestPlanRevision CreatePlan(params TestCharacteristic[] characteristics)
    {
        var planCharacteristics = characteristics.Length == 0
            ? [CreateCharacteristic()]
            : characteristics;

        return TestPlanRevision.Create(
            TestPlanRevisionId.New(),
            TestPlanId.New(),
            revisionNumber: 3,
            "Functional test",
            planCharacteristics,
            BaseTimeUtc);
    }

    public static CalibrationAsset CreateCalibration(
        DateTimeOffset? calibratedAtUtc = null,
        DateTimeOffset? validUntilUtc = null)
    {
        return CalibrationAsset.Create(
            CalibrationAssetId.New(),
            "asset-dmm-01",
            "instrument-dmm-01",
            "certificate-2026-001",
            calibratedAtUtc ?? BaseTimeUtc.AddDays(-1),
            validUntilUtc ?? BaseTimeUtc.AddDays(30));
    }

    public static TestAttempt StartAttempt(TestPlanRevision plan, string unitId = "unit-0001")
    {
        return TestAttempt.Start(
            TestAttemptId.New(),
            plan,
            unitId,
            "station-functional-test",
            attemptNumber: 1,
            BaseTimeUtc);
    }

    public static MeasurementResult Record(
        TestAttempt attempt,
        TestCharacteristic characteristic,
        decimal normalizedValue,
        CalibrationAsset? calibrationAsset = null,
        string? rawValue = null,
        DateTimeOffset? measuredAtUtc = null)
    {
        return attempt.RecordMeasurement(
            MeasurementResultId.New(),
            characteristic.Id,
            rawValue ?? normalizedValue.ToString(CultureInfo.InvariantCulture),
            normalizedValue,
            calibrationAsset ?? CreateCalibration(),
            characteristic.StepVersion,
            EvidenceSha256,
            measuredAtUtc ?? BaseTimeUtc.AddSeconds(1));
    }
}
