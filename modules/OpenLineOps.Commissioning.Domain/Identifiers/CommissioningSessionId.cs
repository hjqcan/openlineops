namespace OpenLineOps.Commissioning.Domain.Identifiers;

public sealed record CommissioningSessionId
{
    public CommissioningSessionId(string value)
    {
        Value = CommissioningGuard.Canonical(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}
