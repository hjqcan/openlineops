using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Infrastructure.Serialization;

namespace OpenLineOps.Plugins.Infrastructure.Discovery;

public static class FileSystemPluginPackageInspector
{
    public static async ValueTask<PluginPackageDescriptor> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Plugin package path is required.", nameof(packagePath));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packagePath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Plugin package directory '{root}' does not exist.");
        }

        RejectReparsePoint(root);
        var files = await InspectTreeAsync(root, cancellationToken).ConfigureAwait(false);
        var manifestFile = files.SingleOrDefault(file => string.Equals(
                file.RelativePath,
                ProjectApplicationPluginPackageReferenceContract.ManifestFileName,
                StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"Plugin package '{root}' must contain exact root file '{ProjectApplicationPluginPackageReferenceContract.ManifestFileName}'.");
        if (files.Any(file => !string.Equals(
                file.RelativePath,
                ProjectApplicationPluginPackageReferenceContract.ManifestFileName,
                StringComparison.Ordinal)
            && string.Equals(
                Path.GetFileName(file.RelativePath),
                ProjectApplicationPluginPackageReferenceContract.ManifestFileName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Plugin package '{root}' contains an ambiguous additional manifest file.");
        }

        var manifestPath = Path.Combine(
            root,
            ProjectApplicationPluginPackageReferenceContract.ManifestFileName);
        PluginManifest manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(
                    stream,
                    PluginJsonContracts.ManifestOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Plugin manifest '{manifestPath}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Plugin manifest '{manifestPath}' contains invalid JSON: {exception.Message}",
                exception);
        }

        var entryAssemblyRelativePath = PluginJsonContracts.RequireCanonicalEntryAssembly(
            manifest.EntryAssembly);
        var entryAssemblyFile = files.SingleOrDefault(file => string.Equals(
                file.RelativePath,
                entryAssemblyRelativePath,
                StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"Plugin entry assembly '{entryAssemblyRelativePath}' is absent or differs by case from its manifest.");

        return new PluginPackageDescriptor(
            manifest,
            root,
            manifestPath,
            ComputePackageContentSha256(files),
            manifestFile.Sha256,
            entryAssemblyFile.Sha256,
            ProjectApplicationPluginPackageReferenceContract.ManifestFileName,
            entryAssemblyRelativePath,
            files);
    }

    public static string ComputePackageContentSha256(
        IReadOnlyCollection<PluginPackageFileDescriptor> files)
    {
        ArgumentNullException.ThrowIfNull(files);
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

    private static async ValueTask<PluginPackageFileDescriptor[]> InspectTreeAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var files = new List<PluginPackageFileDescriptor>();
        var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(packagePath);

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
                var relativePath = GetCanonicalRelativePath(packagePath, entry);
                if (!portablePaths.Add(relativePath))
                {
                    throw new InvalidDataException(
                        $"Plugin package contains duplicate paths or paths differing only by case: '{relativePath}'.");
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                    continue;
                }

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

    private static string GetCanonicalRelativePath(string packagePath, string fullPath)
    {
        var relativePath = Path.GetRelativePath(packagePath, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\')
            || relativePath.Contains(':')
            || relativePath.Any(char.IsControl)
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                $"Plugin package path '{fullPath}' is not a canonical package-relative path.");
        }

        return relativePath;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Plugin package path '{path}' is a reparse point and cannot be inspected.");
        }
    }
}
