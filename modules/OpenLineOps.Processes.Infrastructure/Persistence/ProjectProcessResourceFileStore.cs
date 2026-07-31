using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

internal static class ProjectProcessResourceFileStore
{
    private static readonly ProjectWorkspaceWriteLockPool WriteLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async ValueTask SaveJsonAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        await SaveAtomicAsync(path, bytes, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask SaveNewJsonAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        await SaveAtomicAsync(path, bytes, overwrite: false, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T?> LoadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Project process resource"))
        {
            return default;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                // Readers retain their revision while an atomic replacement commits the next one.
                FileShare.Read | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);

            return await JsonSerializer
                .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Project process resource '{path}' is invalid JSON.", exception);
        }
    }

    public static async ValueTask<ProjectProcessFileReference> SaveArtifactAsync(
        string flowDirectory,
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var sha256 = ComputeSha256(bytes);
        if (!path.Contains(sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Content-addressed artifact path does not contain its digest.");
        }

        await SaveAtomicAsync(path, bytes, overwrite: false, cancellationToken).ConfigureAwait(false);

        return new ProjectProcessFileReference(
            ProjectProcessResourcePath.ToDocumentPath(flowDirectory, path),
            sha256);
    }

    public static async ValueTask<byte[]> LoadVerifiedArtifactAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalSha256(expectedSha256))
        {
            throw new InvalidDataException(
                $"Project process artifact '{path}' declares non-canonical SHA-256 '{expectedSha256}'. " +
                "Expected exactly 64 lowercase hexadecimal characters.");
        }

        if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Project process artifact"))
        {
            throw new InvalidDataException($"Project process artifact '{path}' was not found.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var actualSha256 = ComputeSha256(bytes);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project process artifact '{path}' digest is {actualSha256}, expected {expectedSha256}.");
        }

        return bytes;
    }

    public static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static bool IsCanonicalSha256(string? value)
    {
        return value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static async ValueTask SaveAtomicAsync(
        string path,
        byte[] bytes,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Resource path '{path}' has no parent directory.");
        ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
            directory,
            "Project process resource directory");

        using var writeLock = await WriteLocks.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        Exception? operationFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
                directory,
                "Project process resource directory");
            var targetExists = ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Project process resource");
            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                temporaryPath,
                "Temporary project process resource");

            var writeRequired = true;
            if (!overwrite && targetExists)
            {
                var existingBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!existingBytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidDataException(
                        $"Content-addressed artifact '{path}' already exists with different content.");
                }

                writeRequired = false;
            }

            if (writeRequired)
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    useAsync: true))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    temporaryPath,
                    "Temporary project process resource");
                ProjectWorkspacePathGuard.CreateOrdinaryDirectory(
                    directory,
                    "Project process resource directory");
                targetExists = ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    path,
                    "Project process resource");

                if (overwrite && targetExists)
                {
                    // File.Replace preserves a continuously addressable commit pointer for concurrent readers.
                    File.Replace(temporaryPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, path, overwrite: false);
                }

                if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                        path,
                        "Project process resource"))
                {
                    throw new InvalidDataException(
                        $"Project process resource '{path}' was not committed as an ordinary file.");
                }
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        try
        {
            if (ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    temporaryPath,
                    "Temporary project process resource"))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        ProjectWorkspaceFileOperation.ThrowFailures(
            "Project process resource commit and temporary-file cleanup both failed.",
            operationFailure,
            cleanupFailure);
    }
}
