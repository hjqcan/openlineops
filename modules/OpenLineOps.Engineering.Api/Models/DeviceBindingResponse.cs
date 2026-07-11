namespace OpenLineOps.Engineering.Api.Models;

public sealed record DeviceBindingResponse(
    string DeviceBindingId,
    string OwnerSystemId,
    string CapabilityId,
    string DeviceKey);
