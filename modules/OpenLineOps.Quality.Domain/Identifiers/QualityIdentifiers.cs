using OpenLineOps.Quality.Domain;

namespace OpenLineOps.Quality.Domain.Identifiers;

public readonly record struct TestPlanId
{
    public TestPlanId(Guid value)
    {
        Value = QualityGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static TestPlanId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct TestPlanRevisionId
{
    public TestPlanRevisionId(Guid value)
    {
        Value = QualityGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static TestPlanRevisionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct TestCharacteristicId
{
    public TestCharacteristicId(Guid value)
    {
        Value = QualityGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static TestCharacteristicId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct LimitSetId
{
    public LimitSetId(Guid value)
    {
        Value = QualityGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static LimitSetId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct TestAttemptId
{
    public TestAttemptId(Guid value)
    {
        Value = QualityGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static TestAttemptId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MeasurementResultId
{
    public MeasurementResultId(Guid value)
    {
        Value = QualityGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static MeasurementResultId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct NonconformanceId
{
    public NonconformanceId(Guid value)
    {
        Value = QualityGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static NonconformanceId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct CalibrationAssetId
{
    public CalibrationAssetId(Guid value)
    {
        Value = QualityGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static CalibrationAssetId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
