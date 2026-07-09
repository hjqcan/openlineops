namespace OpenLineOps.Runtime.Domain.Identifiers;

public readonly record struct RuntimeIncidentId
{
    public RuntimeIncidentId(Guid value)
    {
        Value = RuntimeIdGuard.NotEmpty(value, nameof(value));
    }

    public Guid Value { get; }

    public static RuntimeIncidentId New()
    {
        return new RuntimeIncidentId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
