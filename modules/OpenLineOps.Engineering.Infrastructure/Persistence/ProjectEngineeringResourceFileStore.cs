using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

internal static class ProjectEngineeringResourceFileStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task SaveJsonAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Engineering resource path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var writeLock = WriteLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
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

            File.Move(temporaryPath, path, overwrite: true);
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

    public static async Task<T?> LoadJsonAsync<T>(
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
            throw new InvalidDataException(
                $"Project engineering resource '{path}' is invalid JSON.",
                exception);
        }
    }
}
