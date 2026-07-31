using OpenLineOps.Quality.Domain.Nonconformances;

namespace OpenLineOps.Quality.Tests;

public sealed class NonconformanceTests
{
    [Fact]
    public void FailedMeasurementOpensAndDispositionsNonconformance()
    {
        var characteristic = QualityTestData.CreateCharacteristic();
        var attempt = QualityTestData.StartAttempt(QualityTestData.CreatePlan(characteristic));
        var failed = QualityTestData.Record(attempt, characteristic, normalizedValue: 6m);
        var nonconformance = Nonconformance.Open(
            OpenLineOps.Quality.Domain.Identifiers.NonconformanceId.New(),
            attempt,
            failed,
            "voltage.out-of-limit",
            "Supply voltage exceeded its upper limit.",
            NonconformanceSeverity.Major,
            QualityTestData.BaseTimeUtc.AddSeconds(2));

        Assert.Equal(NonconformanceStatus.Open, nonconformance.Status);
        Assert.Equal(attempt.Id, nonconformance.TestAttemptId);
        Assert.Equal(failed.Id, nonconformance.MeasurementResultId);
        Assert.Equal(attempt.ProductionUnitId, nonconformance.ProductionUnitId);

        nonconformance.ApplyDisposition(
            NonconformanceDisposition.Rework,
            "quality-engineer-01",
            "Repeat after connector inspection.",
            QualityTestData.BaseTimeUtc.AddMinutes(1));

        Assert.Equal(NonconformanceStatus.Dispositioned, nonconformance.Status);
        Assert.Equal(NonconformanceDisposition.Rework, nonconformance.Disposition);
        Assert.Equal("quality-engineer-01", nonconformance.DispositionedBy);
        Assert.Equal("Repeat after connector inspection.", nonconformance.DispositionReason);
    }

    [Fact]
    public void PassedMeasurementCannotOpenNonconformance()
    {
        var characteristic = QualityTestData.CreateCharacteristic();
        var attempt = QualityTestData.StartAttempt(QualityTestData.CreatePlan(characteristic));
        var passed = QualityTestData.Record(attempt, characteristic, normalizedValue: 5m);

        Assert.Throws<ArgumentException>(
            () => Nonconformance.Open(
                OpenLineOps.Quality.Domain.Identifiers.NonconformanceId.New(),
                attempt,
                passed,
                "unexpected",
                "A passing result cannot create a nonconformance.",
                NonconformanceSeverity.Minor,
                QualityTestData.BaseTimeUtc.AddSeconds(2)));
    }

    [Fact]
    public void MeasurementMustBelongToSuppliedAttempt()
    {
        var firstCharacteristic = QualityTestData.CreateCharacteristic();
        var firstAttempt = QualityTestData.StartAttempt(
            QualityTestData.CreatePlan(firstCharacteristic),
            "unit-first");
        var failed = QualityTestData.Record(firstAttempt, firstCharacteristic, normalizedValue: 6m);
        var secondAttempt = QualityTestData.StartAttempt(
            QualityTestData.CreatePlan(QualityTestData.CreateCharacteristic()),
            "unit-second");

        Assert.Throws<ArgumentException>(
            () => Nonconformance.Open(
                OpenLineOps.Quality.Domain.Identifiers.NonconformanceId.New(),
                secondAttempt,
                failed,
                "voltage.out-of-limit",
                "Supply voltage exceeded its upper limit.",
                NonconformanceSeverity.Major,
                QualityTestData.BaseTimeUtc.AddSeconds(2)));
    }

    [Fact]
    public void DispositionMustBeValidChronologicalAndAppliedOnlyOnce()
    {
        var characteristic = QualityTestData.CreateCharacteristic();
        var attempt = QualityTestData.StartAttempt(QualityTestData.CreatePlan(characteristic));
        var failed = QualityTestData.Record(attempt, characteristic, normalizedValue: 6m);
        var nonconformance = Nonconformance.Open(
            OpenLineOps.Quality.Domain.Identifiers.NonconformanceId.New(),
            attempt,
            failed,
            "voltage.out-of-limit",
            "Supply voltage exceeded its upper limit.",
            NonconformanceSeverity.Critical,
            QualityTestData.BaseTimeUtc.AddSeconds(2));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => nonconformance.ApplyDisposition(
                NonconformanceDisposition.Scrap,
                "quality-engineer-01",
                "Irrecoverable failure.",
                QualityTestData.BaseTimeUtc));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => nonconformance.ApplyDisposition(
                (NonconformanceDisposition)999,
                "quality-engineer-01",
                "Invalid disposition.",
                QualityTestData.BaseTimeUtc.AddMinutes(1)));

        nonconformance.ApplyDisposition(
            NonconformanceDisposition.Scrap,
            "quality-engineer-01",
            "Irrecoverable failure.",
            QualityTestData.BaseTimeUtc.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(
            () => nonconformance.ApplyDisposition(
                NonconformanceDisposition.Rework,
                "quality-engineer-02",
                "Cannot change an accepted disposition.",
                QualityTestData.BaseTimeUtc.AddMinutes(2)));
    }
}
