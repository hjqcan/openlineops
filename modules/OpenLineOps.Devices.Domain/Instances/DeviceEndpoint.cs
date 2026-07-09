using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Domain.Instances;

public sealed record DeviceEndpoint
{
    public DeviceEndpoint(string protocol, string address)
    {
        Protocol = DeviceIdGuard.NotBlank(protocol, nameof(protocol));
        Address = DeviceIdGuard.NotBlank(address, nameof(address));
    }

    public string Protocol { get; }

    public string Address { get; }
}
