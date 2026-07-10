using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

            var packagePath = Path.GetDirectoryName(manifestPath) ?? _rootDirectory;
            var files = await InspectPackageAsync(packagePath, cancellationToken).ConfigureAwait(false);
            var manifestRelativePath = GetRelativePath(packagePath, manifestPath);
            var entryAssemblyRelativePath = NormalizeRelativePath(manifest.EntryAssembly);
            var entryAssemblyPath = ResolveInsidePackage(packagePath, entryAssemblyRelativePath);
            var manifestFile = files.Single(file => string.Equals(
                file.RelativePath,
                manifestRelativePath,
                StringComparison.Ordinal));
            var entryAssemblyFile = files.SingleOrDefault(file => string.Equals(
                file.RelativePath,
                entryAssemblyRelativePath,
                StringComparison.Ordinal));

            packages.Add(new PluginPackageDescriptor(
                manifest,
                packagePath,
                manifestPath,
                ComputePackageContentSha256(files),
                manifestFile.Sha256,
                entryAssemblyFile?.Sha256 ?? string.Empty,
                manifestRelativePath,
                File.Exists(entryAssemblyPath) ? entryAssemblyRelativePath : string.Empty,
                files));
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

    private static async ValueTask<PluginPackageFileDescriptor[]> InspectPackageAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(packagePath);
        var files = new List<PluginPackageFileDescriptor>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(Path.GetFullPath(packagePath));

        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                    continue;
                }

                var relativePath = GetRelativePath(packagePath, entry);
                await using var stream = new FileStream(
                    entry,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var sha256 = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
                files.Add(new PluginPackageFileDescriptor(
                    relativePath,
                    new FileInfo(entry).Length,
                    sha256));
            }
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string ComputePackageContentSha256(
        IReadOnlyCollection<PluginPackageFileDescriptor> files)
    {
        var canonical = new StringBuilder();
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            canonical.Append(file.RelativePath)
                .Append('\0')
                .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture))
                .Append('\0')
                .Append(file.Sha256)
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string GetRelativePath(string packagePath, string fullPath)
    {
        var relativePath = Path.GetRelativePath(packagePath, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (relativePath.StartsWith("../", StringComparison.Ordinal)
            || string.Equals(relativePath, "..", StringComparison.Ordinal)
            || relativePath.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Plugin package path '{fullPath}' is not a canonical package-relative path.");
        }

        return relativePath;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return string.Empty;
        }

        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string ResolveInsidePackage(string packagePath, string relativePath)
    {
        var root = Path.GetFullPath(packagePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return root;
        }

        var candidate = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            ? candidate
            : root;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Plugin package path '{path}' is a reparse point and cannot be hashed.");
        }
    }
}
