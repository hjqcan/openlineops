namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record DeviceBindingDetails(
    string DeviceBindingId,
    string OwnerSystemId,
    string CapabilityId,
    string DeviceKey);
