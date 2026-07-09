using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Domain.Abstractions.Entities;

namespace OpenLineOps.Devices.Domain.Definitions;

public sealed class DeviceCapability : Entity<DeviceCapabilityId>
{
    private DeviceCapability(DeviceCapabilityId id, string displayName)
        : base(id)
    {
        DisplayName = DeviceIdGuard.NotBlank(displayName, nameof(displayName));
    }

    public string DisplayName { get; }

    public static DeviceCapability Create(DeviceCapabilityId id, string displayName)
    {
        return new DeviceCapability(id, displayName);
    }
}
