namespace OpenLineOps.Traceability.Domain.Identifiers;

public readonly record struct TraceRecordId
{
    public TraceRecordId(Guid value)
    {
        Value = TraceabilityIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static TraceRecordId New()
    {
        return new TraceRecordId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
