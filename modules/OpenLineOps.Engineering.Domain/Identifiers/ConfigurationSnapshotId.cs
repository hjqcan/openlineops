namespace OpenLineOps.Engineering.Domain.Identifiers;

public sealed record ConfigurationSnapshotId
{
    public ConfigurationSnapshotId(string value)
    {
        Value = EngineeringIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
