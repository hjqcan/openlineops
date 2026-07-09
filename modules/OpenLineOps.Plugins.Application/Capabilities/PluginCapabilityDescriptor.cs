using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Application.Capabilities;

public sealed record PluginCapabilityDescriptor(
    string PluginId,
    string PluginName,
    PluginKind PluginKind,
    string Capability);
