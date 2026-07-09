namespace OpenLineOps.Traceability.Domain.Identifiers;

public sealed record ConfigurationSnapshotId
{
    public ConfigurationSnapshotId(string value)
    {
        Value = TraceabilityIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
