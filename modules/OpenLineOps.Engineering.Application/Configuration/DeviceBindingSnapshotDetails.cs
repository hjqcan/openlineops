namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record DeviceBindingSnapshotDetails(
    string DeviceBindingId,
    string OwnerSystemId,
    string CapabilityId,
    string DeviceKey);
