namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record DeviceBindingSnapshotDetails(
    string DeviceBindingId,
    string CapabilityId,
    string DeviceKey);
