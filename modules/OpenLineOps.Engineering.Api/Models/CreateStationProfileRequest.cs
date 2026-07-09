namespace OpenLineOps.Engineering.Api.Models;

public sealed record CreateStationProfileRequest(
    string? StationProfileId,
    string? DisplayName,
    IReadOnlyCollection<CreateDeviceBindingRequest>? DeviceBindings);
