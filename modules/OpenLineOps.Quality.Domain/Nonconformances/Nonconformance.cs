using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.Testing;

namespace OpenLineOps.Quality.Domain.Nonconformances;

public sealed class Nonconformance : AggregateRoot<NonconformanceId>
{
    private Nonconformance(
        NonconformanceId id,
        TestAttempt attempt,
        MeasurementResult measurement,
        string code,
        string description,
        NonconformanceSeverity severity,
        DateTimeOffset raisedAtUtc)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(measurement);

        if (measurement.AttemptId != attempt.Id
            || !attempt.Measurements.Any(candidate => candidate.Id == measurement.Id))
        {
            throw new ArgumentException(
                "Measurement must belong to the supplied test attempt.",
                nameof(measurement));
        }

        if (measurement.Judgement is not (
            MeasurementJudgement.Failed or MeasurementJudgement.Invalid))
        {
            throw new ArgumentException(
                "Only failed or invalid measurements can open a nonconformance.",
                nameof(measurement));
        }

        RaisedAtUtc = QualityGuard.Utc(raisedAtUtc, nameof(raisedAtUtc));
        if (RaisedAtUtc < measurement.MeasuredAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(raisedAtUtc),
                raisedAtUtc,
                "Nonconformance cannot predate its source measurement.");
        }

        TestAttemptId = attempt.Id;
        MeasurementResultId = measurement.Id;
        TestPlanRevisionId = measurement.TestPlanRevisionId;
        TestCharacteristicId = measurement.CharacteristicId;
        ProductionUnitId = attempt.ProductionUnitId;
        Code = QualityGuard.CanonicalText(code, nameof(code));
        Description = QualityGuard.DisplayText(description, nameof(description));
        Severity = QualityGuard.DefinedEnum(severity, nameof(severity));
        Status = NonconformanceStatus.Open;
    }

    public TestAttemptId TestAttemptId { get; }

    public MeasurementResultId MeasurementResultId { get; }

    public TestPlanRevisionId TestPlanRevisionId { get; }

    public TestCharacteristicId TestCharacteristicId { get; }

    public string ProductionUnitId { get; }

    public string Code { get; }

    public string Description { get; }

    public NonconformanceSeverity Severity { get; }

    public NonconformanceStatus Status { get; private set; }

    public DateTimeOffset RaisedAtUtc { get; }

    public NonconformanceDisposition? Disposition { get; private set; }

    public string? DispositionedBy { get; private set; }

    public string? DispositionReason { get; private set; }

    public DateTimeOffset? DispositionedAtUtc { get; private set; }

    public static Nonconformance Open(
        NonconformanceId id,
        TestAttempt attempt,
        MeasurementResult measurement,
        string code,
        string description,
        NonconformanceSeverity severity,
        DateTimeOffset raisedAtUtc)
    {
        return new Nonconformance(
            id,
            attempt,
            measurement,
            code,
            description,
            severity,
            raisedAtUtc);
    }

    public void ApplyDisposition(
        NonconformanceDisposition disposition,
        string dispositionedBy,
        string reason,
        DateTimeOffset dispositionedAtUtc)
    {
        if (Status != NonconformanceStatus.Open)
        {
            throw new InvalidOperationException($"Nonconformance {Id} has already been dispositioned.");
        }

        QualityGuard.DefinedEnum(disposition, nameof(disposition));
        QualityGuard.Utc(dispositionedAtUtc, nameof(dispositionedAtUtc));

        if (dispositionedAtUtc < RaisedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dispositionedAtUtc),
                dispositionedAtUtc,
                "Disposition cannot predate the nonconformance.");
        }

        DispositionedBy = QualityGuard.CanonicalText(dispositionedBy, nameof(dispositionedBy));
        DispositionReason = QualityGuard.DisplayText(reason, nameof(reason));
        DispositionedAtUtc = dispositionedAtUtc;
        Disposition = disposition;
        Status = NonconformanceStatus.Dispositioned;
    }
}
