namespace OpenLineOps.Engineering.Api.Models;

public sealed record DeviceBindingSnapshotResponse(
    string DeviceBindingId,
    string CapabilityId,
    string DeviceKey);
