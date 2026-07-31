using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed record ExternalPluginProcessStartRequest(
    PluginPackageExecutionIdentity ExecutionIdentity,
    PluginManifest Manifest,
    string PackagePath,
    string ManifestPath,
    string EntryAssemblyPath,
    string EntryType,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
