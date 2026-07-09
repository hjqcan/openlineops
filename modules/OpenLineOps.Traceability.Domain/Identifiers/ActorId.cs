namespace OpenLineOps.Traceability.Domain.Identifiers;

public sealed record ActorId
{
    public ActorId(string value)
    {
        Value = TraceabilityIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
