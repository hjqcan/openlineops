namespace OpenLineOps.Traceability.Domain.Identifiers;

public readonly record struct ProductionUnitId
{
    public ProductionUnitId(Guid value)
    {
        Value = TraceabilityIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}
