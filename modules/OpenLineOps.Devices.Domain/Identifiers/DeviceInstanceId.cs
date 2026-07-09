namespace OpenLineOps.Devices.Domain.Identifiers;

public sealed record DeviceInstanceId
{
    public DeviceInstanceId(string value)
    {
        Value = DeviceIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
