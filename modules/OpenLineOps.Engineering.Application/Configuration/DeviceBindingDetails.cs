namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record DeviceBindingDetails(
    string DeviceBindingId,
    string CapabilityId,
    string DeviceKey);
