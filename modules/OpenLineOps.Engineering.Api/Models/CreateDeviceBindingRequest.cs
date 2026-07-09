namespace OpenLineOps.Engineering.Api.Models;

public sealed record CreateDeviceBindingRequest(
    string? DeviceBindingId,
    string? CapabilityId,
    string? DeviceKey);
