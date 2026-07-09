namespace OpenLineOps.Runtime.Domain.Identifiers;

public sealed record RuntimeNodeId
{
    public RuntimeNodeId(string value)
    {
        Value = RuntimeIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
