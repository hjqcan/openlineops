namespace OpenLineOps.Runtime.Domain.Identifiers;

public readonly record struct RuntimeSessionId
{
    public RuntimeSessionId(Guid value)
    {
        Value = RuntimeIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static RuntimeSessionId New()
    {
        return new RuntimeSessionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
