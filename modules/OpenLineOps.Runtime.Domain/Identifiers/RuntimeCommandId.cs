namespace OpenLineOps.Runtime.Domain.Identifiers;

public readonly record struct RuntimeCommandId
{
    public RuntimeCommandId(Guid value)
    {
        Value = RuntimeIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static RuntimeCommandId New()
    {
        return new RuntimeCommandId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
