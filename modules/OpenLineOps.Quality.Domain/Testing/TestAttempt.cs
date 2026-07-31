using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.TestPlans;

namespace OpenLineOps.Quality.Domain.Testing;

public sealed class TestAttempt : AggregateRoot<TestAttemptId>
{
    private readonly List<MeasurementResult> _measurements = [];
    private readonly TestPlanRevision _testPlanRevision;

    private TestAttempt(
        TestAttemptId id,
        TestPlanRevision testPlanRevision,
        string productionUnitId,
        string stationId,
        int attemptNumber,
        DateTimeOffset startedAtUtc)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(testPlanRevision);

        _testPlanRevision = testPlanRevision;
        TestPlanRevisionId = testPlanRevision.Id;
        ProductionUnitId = QualityGuard.CanonicalText(productionUnitId, nameof(productionUnitId));
        StationId = QualityGuard.CanonicalText(stationId, nameof(stationId));
        AttemptNumber = QualityGuard.Positive(attemptNumber, nameof(attemptNumber));
        StartedAtUtc = QualityGuard.Utc(startedAtUtc, nameof(startedAtUtc));
        Status = TestAttemptStatus.Running;
        Judgement = TestAttemptJudgement.Pending;
    }

    public TestPlanRevisionId TestPlanRevisionId { get; }

    public string ProductionUnitId { get; }

    public string StationId { get; }

    public int AttemptNumber { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public TestAttemptStatus Status { get; private set; }

    public TestAttemptJudgement Judgement { get; private set; }

    public string? AbortReason { get; private set; }

    public IReadOnlyCollection<MeasurementResult> Measurements => _measurements.AsReadOnly();

    public static TestAttempt Start(
        TestAttemptId id,
        TestPlanRevision testPlanRevision,
        string productionUnitId,
        string stationId,
        int attemptNumber,
        DateTimeOffset startedAtUtc)
    {
        return new TestAttempt(
            id,
            testPlanRevision,
            productionUnitId,
            stationId,
            attemptNumber,
            startedAtUtc);
    }

    public MeasurementResult RecordMeasurement(
        MeasurementResultId measurementResultId,
        TestCharacteristicId characteristicId,
        string rawValue,
        decimal normalizedValue,
        CalibrationAsset calibrationAsset,
        string stepVersion,
        string evidenceSha256,
        DateTimeOffset measuredAtUtc)
    {
        EnsureRunning();
        QualityGuard.Utc(measuredAtUtc, nameof(measuredAtUtc));

        if (measuredAtUtc < StartedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(measuredAtUtc),
                measuredAtUtc,
                "Measurement cannot predate the test attempt.");
        }

        if (_measurements.Any(measurement => measurement.Id == measurementResultId))
        {
            throw new InvalidOperationException(
                $"Measurement result {measurementResultId} is already part of test attempt {Id}.");
        }

        if (_measurements.Any(measurement => measurement.CharacteristicId == characteristicId))
        {
            throw new InvalidOperationException(
                $"Characteristic {characteristicId} has already been recorded for test attempt {Id}.");
        }

        var characteristic = _testPlanRevision.GetCharacteristic(characteristicId);
        var measurement = MeasurementResult.Create(
            measurementResultId,
            Id,
            TestPlanRevisionId,
            characteristic,
            rawValue,
            normalizedValue,
            calibrationAsset,
            stepVersion,
            evidenceSha256,
            measuredAtUtc);

        _measurements.Add(measurement);

        return measurement;
    }

    public void Complete(DateTimeOffset completedAtUtc)
    {
        EnsureRunning();
        QualityGuard.Utc(completedAtUtc, nameof(completedAtUtc));

        if (completedAtUtc < StartedAtUtc
            || _measurements.Any(measurement => measurement.MeasuredAtUtc > completedAtUtc))
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAtUtc),
                completedAtUtc,
                "Completion cannot predate the attempt or its measurements.");
        }

        var requiredIds = _testPlanRevision.Characteristics
            .Where(characteristic => characteristic.IsRequired)
            .Select(characteristic => characteristic.Id)
            .ToHashSet();
        var measuredRequiredIds = _measurements
            .Where(measurement => requiredIds.Contains(measurement.CharacteristicId))
            .Select(measurement => measurement.CharacteristicId)
            .ToHashSet();

        if (!requiredIds.SetEquals(measuredRequiredIds))
        {
            throw new InvalidOperationException(
                $"Test attempt {Id} cannot complete before all required characteristics are recorded.");
        }

        var requiredMeasurements = _measurements
            .Where(measurement => requiredIds.Contains(measurement.CharacteristicId))
            .ToList();

        Judgement = requiredMeasurements.Any(
                measurement => measurement.Judgement == MeasurementJudgement.Invalid)
            ? TestAttemptJudgement.Invalid
            : requiredMeasurements.Any(
                measurement => measurement.Judgement == MeasurementJudgement.Failed)
                ? TestAttemptJudgement.Failed
                : TestAttemptJudgement.Passed;
        Status = TestAttemptStatus.Completed;
        CompletedAtUtc = completedAtUtc;
    }

    public void Abort(string reason, DateTimeOffset abortedAtUtc)
    {
        EnsureRunning();
        QualityGuard.Utc(abortedAtUtc, nameof(abortedAtUtc));

        if (abortedAtUtc < StartedAtUtc
            || _measurements.Any(measurement => measurement.MeasuredAtUtc > abortedAtUtc))
        {
            throw new ArgumentOutOfRangeException(
                nameof(abortedAtUtc),
                abortedAtUtc,
                "Abort cannot predate the attempt or its measurements.");
        }

        AbortReason = QualityGuard.DisplayText(reason, nameof(reason));
        Status = TestAttemptStatus.Aborted;
        Judgement = TestAttemptJudgement.Invalid;
        CompletedAtUtc = abortedAtUtc;
    }

    private void EnsureRunning()
    {
        if (Status != TestAttemptStatus.Running)
        {
            throw new InvalidOperationException($"Test attempt {Id} is no longer running.");
        }
    }
}
