namespace OpenLineOps.Engineering.Api.Models;

public sealed record StationProfileResponse(
    string StationProfileId,
    string StationSystemId,
    string DisplayName,
    IReadOnlyCollection<DeviceBindingResponse> DeviceBindings);
