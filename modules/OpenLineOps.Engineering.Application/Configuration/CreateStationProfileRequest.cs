namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record CreateStationProfileRequest(
    string StationProfileId,
    string StationSystemId,
    string DisplayName,
    IReadOnlyCollection<CreateDeviceBindingRequest> DeviceBindings);
