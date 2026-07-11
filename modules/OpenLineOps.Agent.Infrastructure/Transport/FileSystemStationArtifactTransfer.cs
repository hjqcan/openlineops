using System.Security.Cryptography;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed record FileSystemStationArtifactTransferOptions(
    string LocalArtifactDirectory,
    string ExchangeDirectory);

public sealed class FileSystemStationArtifactTransfer : IStationArtifactTransfer
{
    private readonly string _localRoot;
    private readonly string _exchangeRoot;

    public FileSystemStationArtifactTransfer(
        FileSystemStationArtifactTransferOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _localRoot = DirectoryRoot(options.LocalArtifactDirectory, nameof(options.LocalArtifactDirectory));
        _exchangeRoot = DirectoryRoot(options.ExchangeDirectory, nameof(options.ExchangeDirectory));
        if (string.Equals(_localRoot, _exchangeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Local and exchanged Station artifact directories must be different.",
                nameof(options));
        }
    }

    public async ValueTask<StationJobArtifact> PublishAsync(
        StationJobId jobId,
        PendingStationJobArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        Validate(jobId, artifact);
        var sourcePath = ResolveLocal(jobId, artifact.LocalArtifactKey);
        await VerifyFileAsync(sourcePath, artifact.SizeBytes, artifact.Sha256, cancellationToken)
            .ConfigureAwait(false);

        var storageKey = $"sha256/{artifact.Sha256[..2]}/{artifact.Sha256}";
        var destinationPath = ResolveInside(_exchangeRoot, storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        RejectReparsePoints(_exchangeRoot, Path.GetDirectoryName(destinationPath)!);
        if (!File.Exists(destinationPath))
        {
            var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.partial";
            try
            {
                await using (var source = OpenRead(sourcePath))
                await using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(temporaryPath, destinationPath);
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        await VerifyFileAsync(
                destinationPath,
                artifact.SizeBytes,
                artifact.Sha256,
                cancellationToken)
            .ConfigureAwait(false);
        File.SetAttributes(
            destinationPath,
            File.GetAttributes(destinationPath) | FileAttributes.ReadOnly);
        return new StationJobArtifact(
            artifact.Name,
            artifact.Kind,
            storageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256);
    }

    public async ValueTask ReleaseLocalAsync(
        StationJobId jobId,
        PendingStationJobArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        Validate(jobId, artifact);
        var sourcePath = ResolveLocal(jobId, artifact.LocalArtifactKey);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        await VerifyFileAsync(sourcePath, artifact.SizeBytes, artifact.Sha256, cancellationToken)
            .ConfigureAwait(false);
        File.SetAttributes(sourcePath, File.GetAttributes(sourcePath) & ~FileAttributes.ReadOnly);
        File.Delete(sourcePath);
        RemoveEmptyLocalDirectories(Path.GetDirectoryName(sourcePath)!);
    }

    private string ResolveLocal(StationJobId jobId, string localArtifactKey)
    {
        var segments = CanonicalSegments(localArtifactKey, nameof(localArtifactKey));
        if (!string.Equals(segments[0], jobId.Value.ToString("N"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Local artifact '{localArtifactKey}' does not belong to Station Job {jobId}.");
        }

        var path = ResolveInside(_localRoot, localArtifactKey);
        RejectReparsePoints(_localRoot, path);
        return path;
    }

    private static async ValueTask VerifyFileAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new FileNotFoundException("Station artifact is missing or is a reparse point.", path);
        }

        if (info.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"Station artifact '{path}' has length {info.Length}, expected {expectedSize}.");
        }

        await using var stream = OpenRead(path);
        var actualSha256 = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station artifact '{path}' does not match SHA-256 {expectedSha256}.");
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void Validate(StationJobId jobId, PendingStationJobArtifact artifact)
    {
        if (jobId.Value == Guid.Empty)
        {
            throw new ArgumentException("Station Job id cannot be empty.", nameof(jobId));
        }

        ArgumentNullException.ThrowIfNull(artifact);
        _ = Required(artifact.Name, nameof(artifact.Name));
        _ = Required(artifact.Kind, nameof(artifact.Kind));
        _ = CanonicalSegments(artifact.LocalArtifactKey, nameof(artifact.LocalArtifactKey));
        if (artifact.MediaType is not null)
        {
            _ = Required(artifact.MediaType, nameof(artifact.MediaType));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(artifact.SizeBytes);
        if (artifact.Sha256.Length != 64
            || artifact.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Station artifact SHA-256 must be lowercase hexadecimal.",
                nameof(artifact));
        }
    }

    private static string DirectoryRoot(string value, string parameterName)
    {
        var root = Path.GetFullPath(Required(value, parameterName))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar)
            .Equals(root, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ArgumentException(
                "Station artifact directory cannot be a filesystem root.",
                parameterName);
        }

        Directory.CreateDirectory(root);
        RejectReparsePoints(root, root);
        return root;
    }

    private static string ResolveInside(string root, string relativePath)
    {
        _ = CanonicalSegments(relativePath, nameof(relativePath));
        var rootedPrefix = root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new InvalidDataException(
                $"Station artifact path '{relativePath}' escapes its configured root.");
    }

    private static string[] CanonicalSegments(string value, string parameterName)
    {
        _ = Required(value, parameterName);
        if (value.Contains('\\') || value.StartsWith('/') || value.EndsWith('/'))
        {
            throw new ArgumentException(
                $"{parameterName} must be a canonical relative path.",
                parameterName);
        }

        var segments = value.Split('/');
        return segments.Any(segment => segment.Length == 0 || segment is "." or "..")
            ? throw new ArgumentException(
                $"{parameterName} contains an unsafe path segment.",
                parameterName)
            : segments;
    }

    private static void RejectReparsePoints(string root, string path)
    {
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var current = Path.GetFullPath(path);
        while (current.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Station artifact path '{current}' contains a reparse point.");
            }

            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException("Station artifact path has no parent directory.");
        }

        throw new InvalidDataException($"Station artifact path '{path}' escapes '{root}'.");
    }

    private void RemoveEmptyLocalDirectories(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!string.Equals(current, _localRoot, StringComparison.OrdinalIgnoreCase)
               && current.StartsWith(
                   _localRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
               && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}
