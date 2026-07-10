namespace OpenLineOps.Runtime.Domain.Identifiers;

public readonly record struct ProductionRunId
{
    public ProductionRunId(Guid value)
    {
        Value = RuntimeIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static ProductionRunId New()
    {
        return new ProductionRunId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
