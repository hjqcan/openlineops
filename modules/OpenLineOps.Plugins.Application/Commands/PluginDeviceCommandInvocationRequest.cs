using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Application.Commands;

public sealed record PluginDeviceCommandInvocationRequest(
    string PluginId,
    string DeviceInstanceId,
    string CommandDefinitionId,
    string Capability,
    string CommandName,
    string? InputPayload,
    int TimeoutMilliseconds,
    PluginPackageRuntimeIdentity? PackageIdentity = null);
