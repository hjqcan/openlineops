namespace OpenLineOps.Devices.Api.Models;

public sealed record DeviceDefinitionResponse(
    string DeviceDefinitionId,
    string DisplayName,
    string PluginId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<DeviceCapabilityResponse> Capabilities,
    IReadOnlyCollection<DeviceCommandDefinitionResponse> Commands);

public sealed record DeviceCapabilityResponse(
    string CapabilityId,
    string DisplayName);

public sealed record DeviceCommandDefinitionResponse(
    string CommandDefinitionId,
    string CapabilityId,
    string CommandName,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutSeconds,
    int MaxRetries);

public sealed record DeviceInstanceResponse(
    string DeviceInstanceId,
    string DeviceDefinitionId,
    string StationId,
    string DisplayName,
    string Protocol,
    string Address,
    DateTimeOffset RegisteredAtUtc,
    string Status,
    DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? LastDisconnectedAtUtc,
    string? FaultReason);

public sealed record CreateDeviceDefinitionRequest(
    string? DeviceDefinitionId,
    string? DisplayName,
    string? PluginId,
    IReadOnlyCollection<CreateDeviceCapabilityRequest>? Capabilities,
    IReadOnlyCollection<CreateDeviceCommandDefinitionRequest>? Commands);

public sealed record CreateDeviceCapabilityRequest(
    string? CapabilityId,
    string? DisplayName);

public sealed record CreateDeviceCommandDefinitionRequest(
    string? CommandDefinitionId,
    string? CapabilityId,
    string? CommandName,
    string? InputSchema,
    string? OutputSchema,
    int? TimeoutSeconds,
    int? MaxRetries);

public sealed record RegisterDeviceInstanceRequest(
    string? DeviceInstanceId,
    string? DeviceDefinitionId,
    string? StationId,
    string? DisplayName,
    string? Protocol,
    string? Address);

public sealed record DeviceStatusChangeRequest(string? Reason = null);
