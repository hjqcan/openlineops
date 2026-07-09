namespace OpenLineOps.Traceability.Domain.Identifiers;

public readonly record struct RuntimeCommandId
{
    public RuntimeCommandId(Guid value)
    {
        Value = TraceabilityIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
