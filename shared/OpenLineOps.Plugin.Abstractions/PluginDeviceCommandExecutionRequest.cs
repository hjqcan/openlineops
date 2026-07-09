namespace OpenLineOps.Plugin.Abstractions;

public sealed record PluginDeviceCommandExecutionRequest(
    string DeviceInstanceId,
    string CommandDefinitionId,
    string Capability,
    string CommandName,
    string? InputPayload,
    TimeSpan Timeout);
