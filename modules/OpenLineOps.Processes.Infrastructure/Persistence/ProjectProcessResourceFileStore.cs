using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

internal static class ProjectProcessResourceFileStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
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
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
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
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Project process artifact '{path}' was not found.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var actualSha256 = ComputeSha256(bytes);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
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

    private static async ValueTask SaveAtomicAsync(
        string path,
        byte[] bytes,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Resource path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var writeLock = WriteLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            if (!overwrite && File.Exists(path))
            {
                var existingBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!existingBytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidDataException(
                        $"Content-addressed artifact '{path}' already exists with different content.");
                }

                return;
            }

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

            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            writeLock.Release();
        }
    }
}
