using System.Reflection;
using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Infrastructure.Serialization;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalPluginHostPluginLoader
{
    public async ValueTask<IOpenLineOpsPlugin> LoadAsync(
        ExternalPluginHostLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var manifestPath = Path.GetFullPath(request.ManifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Plugin manifest '{manifestPath}' was not found.",
                manifestPath);
        }

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer
            .DeserializeAsync<PluginManifest>(
                stream,
                PluginJsonContracts.ManifestOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Plugin manifest '{manifestPath}' is empty or invalid.");

        var packagePath = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException($"Plugin manifest '{manifestPath}' has no package directory.");
        _ = PluginJsonContracts.RequireCanonicalEntryAssembly(manifest.EntryAssembly);
        var entryAssemblyPath = ResolveEntryAssemblyPath(packagePath, manifest, request.EntryAssemblyPath);
        var entryType = string.IsNullOrWhiteSpace(request.EntryType)
            ? manifest.EntryType
            : request.EntryType.Trim();

        var assembly = Assembly.LoadFrom(entryAssemblyPath);
        var pluginType = assembly.GetType(entryType, throwOnError: false, ignoreCase: false);
        if (pluginType is null)
        {
            throw new InvalidOperationException(
                $"Plugin entry type '{entryType}' was not found in '{entryAssemblyPath}'.");
        }

        if (!typeof(IOpenLineOpsPlugin).IsAssignableFrom(pluginType))
        {
            throw new InvalidOperationException(
                $"Plugin entry type '{entryType}' must implement {nameof(IOpenLineOpsPlugin)}.");
        }

        if (Activator.CreateInstance(pluginType) is not IOpenLineOpsPlugin plugin)
        {
            throw new InvalidOperationException(
                $"Plugin entry type '{entryType}' could not be constructed.");
        }

        if (!string.Equals(plugin.Manifest.Id, manifest.Id, StringComparison.Ordinal))
        {
            await plugin.DisposeAsync().ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Plugin manifest id '{plugin.Manifest.Id}' does not match package manifest id '{manifest.Id}'.");
        }

        return plugin;
    }

    private static string ResolveEntryAssemblyPath(
        string packagePath,
        PluginManifest manifest,
        string? entryAssemblyOverride)
    {
        var packageDirectory = Path.GetFullPath(packagePath);
        var entryAssemblyPath = string.IsNullOrWhiteSpace(entryAssemblyOverride)
            ? Path.GetFullPath(Path.Combine(packageDirectory, manifest.EntryAssembly))
            : Path.GetFullPath(entryAssemblyOverride);

        if (!IsPathInsideDirectory(entryAssemblyPath, packageDirectory))
        {
            throw new InvalidOperationException(
                $"Plugin entry assembly '{entryAssemblyPath}' is outside package directory '{packageDirectory}'.");
        }

        if (!File.Exists(entryAssemblyPath))
        {
            throw new FileNotFoundException(
                $"Plugin entry assembly '{entryAssemblyPath}' was not found.",
                entryAssemblyPath);
        }

        return entryAssemblyPath;
    }

    private static bool IsPathInsideDirectory(string candidatePath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, candidatePath);

        return !relativePath.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }
}
