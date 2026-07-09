namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record CreateDeviceBindingRequest(
    string DeviceBindingId,
    string CapabilityId,
    string DeviceKey);
