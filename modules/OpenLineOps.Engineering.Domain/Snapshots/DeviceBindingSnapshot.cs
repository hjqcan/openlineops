using OpenLineOps.Engineering.Domain.Identifiers;

namespace OpenLineOps.Engineering.Domain.Snapshots;

public sealed record DeviceBindingSnapshot(
    DeviceBindingId DeviceBindingId,
    string OwnerSystemId,
    DeviceCapabilityId CapabilityId,
    string DeviceKey);
