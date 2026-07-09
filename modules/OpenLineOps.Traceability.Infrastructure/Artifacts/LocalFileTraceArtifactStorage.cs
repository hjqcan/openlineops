using System.Security.Cryptography;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Application.Artifacts;

namespace OpenLineOps.Traceability.Infrastructure.Artifacts;

public sealed class LocalFileTraceArtifactStorage : ITraceArtifactStorage
{
    private const int BufferSize = 81920;

    private readonly string _rootPath;

    public LocalFileTraceArtifactStorage(string rootPath)
    {
        _rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? throw new ArgumentException("Artifact storage root path is required.", nameof(rootPath))
            : Path.GetFullPath(rootPath.Trim());
    }

    public async Task<Result<StoredTraceArtifact>> StoreAsync(
        StoreTraceArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationError = ValidateStoreRequest(request);
        if (validationError is not null)
        {
            return Result.Failure<StoredTraceArtifact>(validationError);
        }

        string storageKey;
        try
        {
            storageKey = request.StorageKey is null
                ? GenerateStorageKey(request.FileName)
                : NormalizeStorageKey(request.StorageKey);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<StoredTraceArtifact>(ApplicationError.Validation(
                "Traceability.ArtifactStorageKeyInvalid",
                exception.Message));
        }

        var resolvedPath = ResolveArtifactPath(storageKey);
        if (resolvedPath.IsFailure)
        {
            return Result.Failure<StoredTraceArtifact>(resolvedPath.Error);
        }

        try
        {
            var directory = Path.GetDirectoryName(resolvedPath.Value);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var destination = new FileStream(
                resolvedPath.Value,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            var buffer = new byte[BufferSize];
            long sizeBytes = 0;

            while (true)
            {
                var bytesRead = await request.Content
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                sizeBytes += bytesRead;
            }

            sha256.TransformFinalBlock([], 0, 0);
            var sha256Hex = Convert.ToHexString(sha256.Hash!);

            if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
                && !string.Equals(sha256Hex, request.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                destination.Close();
                File.Delete(resolvedPath.Value);

                return Result.Failure<StoredTraceArtifact>(ApplicationError.Validation(
                    "Traceability.ArtifactSha256Mismatch",
                    "Stored artifact content does not match the expected SHA-256 hash."));
            }

            return Result.Success(new StoredTraceArtifact(
                storageKey,
                NormalizeFileName(request.FileName),
                NormalizeOptional(request.MediaType),
                sizeBytes,
                sha256Hex));
        }
        catch (IOException exception) when (File.Exists(resolvedPath.Value))
        {
            return Result.Failure<StoredTraceArtifact>(ApplicationError.Conflict(
                "Traceability.ArtifactAlreadyExists",
                $"Artifact storage key {storageKey} already exists: {exception.Message}"));
        }
    }

    public Task<Result<TraceArtifactContent>> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return Task.FromResult(Result.Failure<TraceArtifactContent>(ApplicationError.Validation(
                "Traceability.ArtifactStorageKeyRequired",
                "Artifact storage key is required.")));
        }

        string normalizedStorageKey;
        try
        {
            normalizedStorageKey = NormalizeStorageKey(storageKey);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(Result.Failure<TraceArtifactContent>(ApplicationError.Validation(
                "Traceability.ArtifactStorageKeyInvalid",
                exception.Message)));
        }

        var resolvedPath = ResolveArtifactPath(normalizedStorageKey);
        if (resolvedPath.IsFailure)
        {
            return Task.FromResult(Result.Failure<TraceArtifactContent>(resolvedPath.Error));
        }

        if (!File.Exists(resolvedPath.Value))
        {
            return Task.FromResult(Result.Failure<TraceArtifactContent>(ApplicationError.NotFound(
                "Traceability.ArtifactNotFound",
                $"Artifact {normalizedStorageKey} was not found.")));
        }

        var stream = new FileStream(
            resolvedPath.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fileInfo = new FileInfo(resolvedPath.Value);

        return Task.FromResult(Result.Success(new TraceArtifactContent(
            normalizedStorageKey,
            Path.GetFileName(resolvedPath.Value),
            InferMediaType(resolvedPath.Value),
            fileInfo.Length,
            string.Empty,
            stream)));
    }

    private Result<string> ResolveArtifactPath(string storageKey)
    {
        var relativePath = storageKey.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        var rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<string>(ApplicationError.Validation(
                "Traceability.ArtifactStorageKeyInvalid",
                "Artifact storage key must resolve under the configured artifact storage root."));
        }

        return Result.Success(fullPath);
    }

    private static ApplicationError? ValidateStoreRequest(StoreTraceArtifactRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactFileNameRequired",
                "Artifact file name is required.");
        }

        if (request.Content is null)
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactContentRequired",
                "Artifact content is required.");
        }

        return null;
    }

    private static string GenerateStorageKey(string fileName)
    {
        var safeFileName = NormalizeFileName(fileName);
        var extension = Path.GetExtension(safeFileName);
        var generatedName = string.IsNullOrWhiteSpace(extension)
            ? Guid.NewGuid().ToString("N")
            : $"{Guid.NewGuid():N}{extension}";

        return $"{DateTimeOffset.UtcNow:yyyy/MM/dd}/{generatedName}";
    }

    private static string NormalizeStorageKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Artifact storage key is required.", nameof(storageKey));
        }

        if (storageKey.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Artifact storage key must use '/' as the path separator.", nameof(storageKey));
        }

        var segments = storageKey
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Artifact storage key cannot contain empty or relative path segments.", nameof(storageKey));
        }

        if (segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("Artifact storage key contains invalid file name characters.", nameof(storageKey));
        }

        return string.Join('/', segments);
    }

    private static string NormalizeFileName(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName.Trim());
        return string.IsNullOrWhiteSpace(safeFileName)
            ? throw new ArgumentException("Artifact file name is required.", nameof(fileName))
            : safeFileName;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? InferMediaType(string path)
    {
        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".CSV" => "text/csv",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".JSON" => "application/json",
            ".LOG" or ".TXT" => "text/plain",
            ".PDF" => "application/pdf",
            ".PNG" => "image/png",
            ".ZIP" => "application/zip",
            _ => null
        };
    }
}
