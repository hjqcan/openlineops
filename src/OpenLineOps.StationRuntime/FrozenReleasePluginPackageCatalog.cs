using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Infrastructure.Discovery;
using OpenLineOps.Projects.Application.Releases;

namespace OpenLineOps.StationRuntime;

internal sealed class FrozenReleasePluginPackageCatalog : IPluginPackageCatalog
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly PluginPackageDescriptor[] _packages;
    private readonly string _projectId;
    private readonly string _applicationId;

    private FrozenReleasePluginPackageCatalog(
        string packageRootPath,
        string projectId,
        string applicationId,
        PluginPackageDescriptor[] packages)
    {
        PackageRootPath = packageRootPath;
        _projectId = projectId;
        _applicationId = applicationId;
        _packages = packages;
    }

    public string PackageRootPath { get; }

    public IReadOnlyCollection<PluginPackageDescriptor> Packages => _packages;

    public static async ValueTask<FrozenReleasePluginPackageCatalog> OpenAsync(
        OpenedProjectReleaseArtifact release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        var releaseRoot = Path.GetFullPath(release.ReleaseRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourceRoot = Path.GetFullPath(release.SourceRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(
                sourceRoot,
                Path.Combine(releaseRoot, "source"),
                PathComparison))
        {
            throw new InvalidDataException(
                "Frozen release SourceRootPath is not the canonical release source directory.");
        }

        var packageRoot = Path.Combine(releaseRoot, "packages");
        var packageDirectories = Directory.Exists(packageRoot)
            ? Directory.EnumerateDirectories(packageRoot, "*", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        var discoveredPackages = new List<PluginPackageDescriptor>(packageDirectories.Length);
        foreach (var packageDirectory in packageDirectories)
        {
            discoveredPackages.Add(await FileSystemPluginPackageInspector
                .InspectAsync(packageDirectory, cancellationToken)
                .ConfigureAwait(false));
        }

        var discovered = discoveredPackages
            .OrderBy(package => package.PackageContentSha256, StringComparer.Ordinal)
            .ToArray();
        ValidatePackages(release, releaseRoot, discovered);
        return new FrozenReleasePluginPackageCatalog(
            packageRoot,
            release.ProjectId,
            release.ApplicationId,
            discovered);
    }

    public ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(scope.ProjectId, _projectId, StringComparison.Ordinal)
            || !string.Equals(scope.ApplicationId, _applicationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Frozen release plugin catalog cannot be used for another Project/Application scope.");
        }

        return ValueTask.FromResult<IReadOnlyCollection<PluginPackageDescriptor>>(_packages);
    }

    private static void ValidatePackages(
        OpenedProjectReleaseArtifact release,
        string releaseRoot,
        PluginPackageDescriptor[] discovered)
    {
        var expected = release.Metadata.PackageDependencies
            .GroupBy(dependency => dependency.PackageContentSha256, StringComparer.Ordinal)
            .Select(group => RequireConsistentLock(group.ToArray()))
            .OrderBy(dependency => dependency.PackageContentSha256, StringComparer.Ordinal)
            .ToArray();
        if (expected.Length != discovered.Length)
        {
            throw new InvalidDataException(
                "Frozen release package directory does not contain exactly the locked plugin package set.");
        }

        if (discovered.Select(package => package.Manifest.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != discovered.Length)
        {
            throw new InvalidDataException(
                "Frozen release cannot activate more than one package for the same Plugin identity.");
        }

        foreach (var packageLock in expected)
        {
            var matches = discovered.Where(package => string.Equals(
                    package.PackageContentSha256,
                    packageLock.PackageContentSha256,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    $"Frozen plugin package '{packageLock.PackageContentSha256}' is missing or duplicated.");
            }

            ValidatePackage(releaseRoot, packageLock, matches[0]);
        }
    }

    private static ProjectReleasePackageDependencyLock RequireConsistentLock(
        IReadOnlyCollection<ProjectReleasePackageDependencyLock> locks)
    {
        var expected = locks.First();
        if (locks.Any(candidate => !SamePackageLock(expected, candidate)))
        {
            throw new InvalidDataException(
                $"Frozen release contains conflicting locks for package '{expected.PackageContentSha256}'.");
        }

        return expected;
    }

    private static bool SamePackageLock(
        ProjectReleasePackageDependencyLock left,
        ProjectReleasePackageDependencyLock right)
    {
        return string.Equals(left.PackageId, right.PackageId, StringComparison.Ordinal)
            && string.Equals(left.PluginId, right.PluginId, StringComparison.Ordinal)
            && string.Equals(left.PackageVersion, right.PackageVersion, StringComparison.Ordinal)
            && string.Equals(left.PackageContentSha256, right.PackageContentSha256, StringComparison.Ordinal)
            && string.Equals(left.ManifestSha256, right.ManifestSha256, StringComparison.Ordinal)
            && string.Equals(left.EntryAssemblySha256, right.EntryAssemblySha256, StringComparison.Ordinal)
            && string.Equals(left.ContractVersion, right.ContractVersion, StringComparison.Ordinal)
            && string.Equals(left.RuntimeIdentifier, right.RuntimeIdentifier, StringComparison.Ordinal)
            && string.Equals(left.AbiVersion, right.AbiVersion, StringComparison.Ordinal)
            && string.Equals(left.PackageRelativePath, right.PackageRelativePath, StringComparison.Ordinal)
            && string.Equals(left.ManifestRelativePath, right.ManifestRelativePath, StringComparison.Ordinal)
            && string.Equals(left.EntryAssemblyRelativePath, right.EntryAssemblyRelativePath, StringComparison.Ordinal)
            && left.Files.SequenceEqual(right.Files);
    }

    private static void ValidatePackage(
        string releaseRoot,
        ProjectReleasePackageDependencyLock packageLock,
        PluginPackageDescriptor package)
    {
        var expectedPackagePath = ResolveInsideRelease(releaseRoot, packageLock.PackageRelativePath);
        var expectedManifestPath = ResolveInsidePackage(
            expectedPackagePath,
            packageLock.ManifestRelativePath);
        var expectedEntryAssemblyPath = ResolveInsidePackage(
            expectedPackagePath,
            packageLock.EntryAssemblyRelativePath);
        var actualEntryAssemblyPath = ResolveInsidePackage(
            package.PackagePath,
            package.EntryAssemblyRelativePath);
        var files = package.Files?.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray()
            ?? [];
        var lockedFiles = packageLock.Files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        if (!string.Equals(package.PackagePath, expectedPackagePath, PathComparison)
            || !string.Equals(package.ManifestPath, expectedManifestPath, PathComparison)
            || !string.Equals(actualEntryAssemblyPath, expectedEntryAssemblyPath, PathComparison)
            || !string.Equals(package.Manifest.Id, packageLock.PluginId, StringComparison.Ordinal)
            || !string.Equals(package.Manifest.Version, packageLock.PackageVersion, StringComparison.Ordinal)
            || !string.Equals(package.PackageContentSha256, packageLock.PackageContentSha256, StringComparison.Ordinal)
            || !string.Equals(package.ManifestSha256, packageLock.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(package.EntryAssemblySha256, packageLock.EntryAssemblySha256, StringComparison.Ordinal)
            || !string.Equals(package.Manifest.ContractVersion, packageLock.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(package.Manifest.RuntimeIdentifier, packageLock.RuntimeIdentifier, StringComparison.Ordinal)
            || !string.Equals(package.Manifest.AbiVersion, packageLock.AbiVersion, StringComparison.Ordinal)
            || !string.Equals(package.ManifestRelativePath, packageLock.ManifestRelativePath, StringComparison.Ordinal)
            || !string.Equals(package.EntryAssemblyRelativePath, packageLock.EntryAssemblyRelativePath, StringComparison.Ordinal)
            || files.Length != lockedFiles.Length
            || files.Where((file, index) =>
                    !string.Equals(file.RelativePath, lockedFiles[index].RelativePath, StringComparison.Ordinal)
                    || file.SizeBytes != lockedFiles[index].SizeBytes
                    || !string.Equals(file.Sha256, lockedFiles[index].Sha256, StringComparison.Ordinal))
                .Any())
        {
            throw new InvalidDataException(
                $"Frozen plugin package '{packageLock.PluginId}' identity, hash, path, or file inventory differs from its release lock.");
        }
    }

    private static string ResolveInsideRelease(string releaseRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
        {
            throw new InvalidDataException("Frozen plugin package path is not canonical.");
        }

        var path = Path.GetFullPath(Path.Combine(
            releaseRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(releaseRoot + Path.DirectorySeparatorChar, PathComparison)
            ? path
            : throw new InvalidDataException("Frozen plugin package path escapes the release root.");
    }

    private static string ResolveInsidePackage(string packageRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
        {
            throw new InvalidDataException("Frozen plugin file path is not canonical.");
        }

        var root = Path.GetFullPath(packageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)
            ? path
            : throw new InvalidDataException("Frozen plugin file path escapes its package.");
    }
}
