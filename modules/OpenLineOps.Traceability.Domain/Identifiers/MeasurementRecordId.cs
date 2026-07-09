namespace OpenLineOps.Traceability.Domain.Identifiers;

public readonly record struct MeasurementRecordId
{
    public MeasurementRecordId(Guid value)
    {
        Value = TraceabilityIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static MeasurementRecordId New()
    {
        return new MeasurementRecordId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
