using OpenLineOps.Plugin.Abstractions;

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
    int MaxRetries);
