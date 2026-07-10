namespace OpenLineOps.Engineering.Api.Models;

public sealed record CreateStationProfileRequest(
    string? StationProfileId,
    string? StationSystemId,
    string? DisplayName,
    IReadOnlyCollection<CreateDeviceBindingRequest>? DeviceBindings);
