namespace OpenLineOps.Devices.Application.Configuration;

public sealed record DeviceDefinitionDetails(
    string DeviceDefinitionId,
    string DisplayName,
    string PluginId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<DeviceCapabilityDetails> Capabilities,
    IReadOnlyCollection<DeviceCommandDefinitionDetails> Commands);

public sealed record DeviceCapabilityDetails(
    string CapabilityId,
    string DisplayName);

public sealed record DeviceCommandDefinitionDetails(
    string CommandDefinitionId,
    string CapabilityId,
    string CommandName,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutSeconds,
    int MaxRetries);

public sealed record DeviceInstanceDetails(
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
