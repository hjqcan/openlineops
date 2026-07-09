using OpenLineOps.Engineering.Domain.Identifiers;

namespace OpenLineOps.Engineering.Domain.Snapshots;

public sealed record DeviceBindingSnapshot(
    DeviceBindingId DeviceBindingId,
    DeviceCapabilityId CapabilityId,
    string DeviceKey);
