using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Application.Commands;

public sealed record PluginDeviceCommandDescriptor(
    string PluginId,
    string PluginName,
    PluginKind PluginKind,
    string CommandDefinitionId,
    string Capability,
    string CommandName,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutMilliseconds,
    int MaxRetries,
    PluginPackageRuntimeIdentity? PackageIdentity = null);
