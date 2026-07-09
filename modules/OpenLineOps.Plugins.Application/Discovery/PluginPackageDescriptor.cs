using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Application.Discovery;

public sealed record PluginPackageDescriptor(
    PluginManifest Manifest,
    string PackagePath,
    string ManifestPath);
