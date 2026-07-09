using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Engineering.Domain.Identifiers;

namespace OpenLineOps.Engineering.Domain.Stations;

public sealed class DeviceBinding : Entity<DeviceBindingId>
{
    private DeviceBinding(DeviceBindingId id, DeviceCapabilityId capabilityId, string deviceKey)
        : base(id)
    {
        CapabilityId = capabilityId;
        DeviceKey = EngineeringIdGuard.NotBlank(deviceKey, nameof(deviceKey));
    }

    public DeviceCapabilityId CapabilityId { get; }

    public string DeviceKey { get; }

    public static DeviceBinding Create(DeviceBindingId id, DeviceCapabilityId capabilityId, string deviceKey)
    {
        return new DeviceBinding(id, capabilityId, deviceKey);
    }
}
