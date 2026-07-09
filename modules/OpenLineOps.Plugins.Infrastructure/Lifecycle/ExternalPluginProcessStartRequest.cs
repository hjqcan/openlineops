using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed record ExternalPluginProcessStartRequest(
    PluginManifest Manifest,
    string PackagePath,
    string ManifestPath,
    string EntryAssemblyPath,
    string EntryType,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
