namespace OpenLineOps.Engineering.Api.Models;

public sealed record DeviceBindingSnapshotResponse(
    string DeviceBindingId,
    string OwnerSystemId,
    string CapabilityId,
    string DeviceKey);
