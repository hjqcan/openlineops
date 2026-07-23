using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Discovery;

public sealed class FileSystemPluginPackageCatalog(
    IProjectApplicationPluginPackageReferenceStore referenceStore) : IPluginPackageCatalog
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public async ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var references = ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
            await referenceStore.ReadAsync(scope, cancellationToken).ConfigureAwait(false));
        if (references.Length == 0)
        {
            return [];
        }

        EnsureDirectory(scope.ApplicationRootPath, "Application root");
        EnsureDirectory(scope.PluginsRootPath, "Application plugins directory");
        var packages = new List<PluginPackageDescriptor>(references.Length);
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var portableId = ProjectApplicationPluginPackageReferenceContract.GetPortableId(
                reference.ManifestPath);
            var packagePath = ResolveInsidePluginsRoot(scope.PluginsRootPath, portableId);
            var expectedManifestPath = Path.Combine(
                packagePath,
                ProjectApplicationPluginPackageReferenceContract.ManifestFileName);
            var descriptor = await FileSystemPluginPackageInspector
                .InspectAsync(packagePath, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(descriptor.ManifestPath, expectedManifestPath, PathComparison)
                || !string.Equals(descriptor.Manifest.Id, reference.PluginId, StringComparison.Ordinal)
                || !string.Equals(descriptor.Manifest.Version, reference.Version, StringComparison.Ordinal)
                || !string.Equals(
                    descriptor.PackageContentSha256,
                    reference.ContentSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Application plugin reference '{reference.PluginId}' does not match its exact package identity, version, path, or full-tree SHA-256.");
            }

            packages.Add(descriptor);
        }

        return packages
            .OrderBy(package => package.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveInsidePluginsRoot(string pluginsRootPath, string portableId)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRootPath));
        var candidate = Path.GetFullPath(Path.Combine(root, portableId));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException("Application plugin package path escapes the plugins directory.");
        }

        return candidate;
    }

    private static void EnsureDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{description} '{path}' does not exist.");
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} '{path}' is a reparse point.");
        }
    }
}
