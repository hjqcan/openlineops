namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record StationProfileDetails(
    string StationProfileId,
    string StationSystemId,
    string DisplayName,
    IReadOnlyCollection<DeviceBindingDetails> DeviceBindings);
