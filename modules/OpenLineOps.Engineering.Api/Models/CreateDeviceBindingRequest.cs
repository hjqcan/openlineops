namespace OpenLineOps.Engineering.Api.Models;

public sealed record CreateDeviceBindingRequest(
    string? DeviceBindingId,
    string? OwnerSystemId,
    string? CapabilityId,
    string? DeviceKey);
