using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Discovery;

public sealed class FileSystemPluginPackageCatalog : IPluginPackageCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly string _rootDirectory;
    private readonly HashSet<string> _manifestFileNames;

    public FileSystemPluginPackageCatalog(
        string rootDirectory,
        IEnumerable<string>? manifestFileNames = null)
    {
        _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("Plugin root directory is required.", nameof(rootDirectory))
            : Path.GetFullPath(rootDirectory);
        _manifestFileNames = new HashSet<string>(
            manifestFileNames ?? ["openlineops-plugin.json", "plugin.manifest.json", "plugin.json", "manifest.json"],
            StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        List<PluginPackageDescriptor> packages = [];

        foreach (var manifestPath in EnumerateManifestPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer
                .DeserializeAsync<PluginManifest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (manifest is null)
            {
                throw new InvalidDataException($"Plugin manifest '{manifestPath}' is empty or invalid.");
            }

            packages.Add(new PluginPackageDescriptor(
                manifest,
                Path.GetDirectoryName(manifestPath) ?? _rootDirectory,
                manifestPath));
        }

        return packages
            .OrderBy(package => package.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private IEnumerable<string> EnumerateManifestPaths()
    {
        return Directory
            .EnumerateFiles(_rootDirectory, "*.json", SearchOption.AllDirectories)
            .Where(path => _manifestFileNames.Contains(Path.GetFileName(path)))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal);
    }
}
