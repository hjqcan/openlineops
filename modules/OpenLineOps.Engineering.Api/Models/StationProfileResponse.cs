namespace OpenLineOps.Engineering.Api.Models;

public sealed record StationProfileResponse(
    string StationProfileId,
    string DisplayName,
    IReadOnlyCollection<DeviceBindingResponse> DeviceBindings);
