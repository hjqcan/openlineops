namespace OpenLineOps.Runtime.Domain.Identifiers;

public readonly record struct RuntimeStepId
{
    public RuntimeStepId(Guid value)
    {
        Value = RuntimeIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static RuntimeStepId New()
    {
        return new RuntimeStepId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
