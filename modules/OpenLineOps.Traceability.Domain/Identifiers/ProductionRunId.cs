namespace OpenLineOps.Traceability.Domain.Identifiers;

public readonly record struct ProductionRunId
{
    public ProductionRunId(Guid value)
    {
        Value = TraceabilityIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
