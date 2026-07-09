namespace OpenLineOps.Devices.Domain.Identifiers;

public sealed record DeviceDefinitionId
{
    public DeviceDefinitionId(string value)
    {
        Value = DeviceIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
