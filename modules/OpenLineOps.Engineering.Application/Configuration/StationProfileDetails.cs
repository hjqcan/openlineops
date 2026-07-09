namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record StationProfileDetails(
    string StationProfileId,
    string DisplayName,
    IReadOnlyCollection<DeviceBindingDetails> DeviceBindings);
