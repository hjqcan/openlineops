namespace OpenLineOps.Engineering.Api.Models;

public sealed record DeviceBindingResponse(
    string DeviceBindingId,
    string CapabilityId,
    string DeviceKey);
