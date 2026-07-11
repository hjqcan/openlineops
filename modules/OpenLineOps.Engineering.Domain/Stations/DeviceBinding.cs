using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Engineering.Domain.Identifiers;

namespace OpenLineOps.Engineering.Domain.Stations;

public sealed class DeviceBinding : Entity<DeviceBindingId>
{
    private DeviceBinding(
        DeviceBindingId id,
        string ownerSystemId,
        DeviceCapabilityId capabilityId,
        string deviceKey)
        : base(id)
    {
        OwnerSystemId = EngineeringIdGuard.NotBlank(ownerSystemId, nameof(ownerSystemId));
        CapabilityId = capabilityId;
        DeviceKey = EngineeringIdGuard.NotBlank(deviceKey, nameof(deviceKey));
    }

    public string OwnerSystemId { get; }

    public DeviceCapabilityId CapabilityId { get; }

    public string DeviceKey { get; }

    public static DeviceBinding Create(
        DeviceBindingId id,
        string ownerSystemId,
        DeviceCapabilityId capabilityId,
        string deviceKey)
    {
        return new DeviceBinding(id, ownerSystemId, capabilityId, deviceKey);
    }
}
