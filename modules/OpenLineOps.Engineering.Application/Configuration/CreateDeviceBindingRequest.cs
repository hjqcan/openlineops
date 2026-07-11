namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record CreateDeviceBindingRequest(
    string DeviceBindingId,
    string OwnerSystemId,
    string CapabilityId,
    string DeviceKey);
