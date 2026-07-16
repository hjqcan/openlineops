using System.Security.Cryptography;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Application.Artifacts;

namespace OpenLineOps.Traceability.Infrastructure.Artifacts;

public sealed class FileSystemTraceArtifactStorage : ITraceArtifactStorage
{
    private const int BufferSize = 81920;
    private const long DefaultMaximumArtifactSizeBytes = 256L * 1024L * 1024L;

    private readonly string _rootPath;
    private readonly long _maximumArtifactSizeBytes;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public FileSystemTraceArtifactStorage(
        string rootPath,
        long maximumArtifactSizeBytes = DefaultMaximumArtifactSizeBytes)
    {
        if (maximumArtifactSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumArtifactSizeBytes),
                "Artifact storage maximum size must be greater than zero.");
        }

        var fullRootPath = !IsCanonicalText(rootPath)
            ? throw new ArgumentException("Artifact storage root path is required.", nameof(rootPath))
            : Path.GetFullPath(rootPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(fullRootPath)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.Equals(
                fullRootPath,
                volumeRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Artifact storage root cannot be a filesystem volume root.",
                nameof(rootPath));
        }

        Directory.CreateDirectory(fullRootPath);
        RejectReparsePointsFromVolumeRoot(fullRootPath);
        _rootPath = fullRootPath;
        _maximumArtifactSizeBytes = maximumArtifactSizeBytes;
    }

    public async Task<Result<StoredTraceArtifact>> StoreAsync(
        StoreTraceArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationError = ValidateStoreRequest(request, _maximumArtifactSizeBytes);
        if (validationError is not null)
        {
            return Result.Failure<StoredTraceArtifact>(validationError);
        }

        string storageKey;
        try
        {
            storageKey = request.StorageKey is null
                ? GenerateStorageKey(request.FileName)
                : ValidateStorageKey(request.StorageKey);
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

        var directory = Path.GetDirectoryName(resolvedPath.Value)!;
        try
        {
            EnsureSafeDirectory(directory);
            RejectReparseTarget(resolvedPath.Value);
        }
        catch (Exception exception) when (exception is IOException
                                            or UnauthorizedAccessException
                                            or InvalidDataException)
        {
            return Result.Failure<StoredTraceArtifact>(ApplicationError.Validation(
                "Traceability.ArtifactPathUnsafe",
                exception.Message));
        }
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(resolvedPath.Value)}.{Guid.NewGuid():N}.uploading");
        try
        {
            var incoming = await WriteIncomingAsync(
                    request,
                    _maximumArtifactSizeBytes,
                    temporaryPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (incoming.IsFailure)
            {
                return Result.Failure<StoredTraceArtifact>(incoming.Error);
            }

            EnsureSafeDirectory(directory);
            RejectReparseTarget(resolvedPath.Value);
            if (File.Exists(resolvedPath.Value))
            {
                return await MatchExistingAsync(
                        request,
                        storageKey,
                        resolvedPath.Value,
                        incoming.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            File.SetAttributes(
                temporaryPath,
                File.GetAttributes(temporaryPath) | FileAttributes.ReadOnly);
            try
            {
                EnsureSafeDirectory(directory);
                RejectReparseTarget(resolvedPath.Value);
                File.Move(temporaryPath, resolvedPath.Value);
            }
            catch (IOException) when (File.Exists(resolvedPath.Value))
            {
                return await MatchExistingAsync(
                        request,
                        storageKey,
                        resolvedPath.Value,
                        incoming.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return Result.Success(ToStored(request, storageKey, incoming.Value));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.SetAttributes(
                    temporaryPath,
                    File.GetAttributes(temporaryPath) & ~FileAttributes.ReadOnly);
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<Result<ArtifactDigest>> WriteIncomingAsync(
        StoreTraceArtifactRequest request,
        long maximumArtifactSizeBytes,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
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

            sizeBytes = checked(sizeBytes + bytesRead);
            if (sizeBytes > maximumArtifactSizeBytes)
            {
                return Result.Failure<ArtifactDigest>(ApplicationError.Validation(
                    "Traceability.ArtifactSizeLimitExceeded",
                    $"Artifact content exceeds the configured {maximumArtifactSizeBytes}-byte limit."));
            }

            if (request.ExpectedSizeBytes is { } expectedSize && sizeBytes > expectedSize)
            {
                return Result.Failure<ArtifactDigest>(ApplicationError.Validation(
                    "Traceability.ArtifactSizeMismatch",
                    "Artifact content exceeds its declared size."));
            }

            await destination.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken)
                .ConfigureAwait(false);
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        }

        destination.Flush(flushToDisk: true);
        sha256.TransformFinalBlock([], 0, 0);
        var digest = new ArtifactDigest(
            sizeBytes,
            Convert.ToHexStringLower(sha256.Hash!));
        if (request.ExpectedSizeBytes is { } exactSize && digest.SizeBytes != exactSize)
        {
            return Result.Failure<ArtifactDigest>(ApplicationError.Validation(
                "Traceability.ArtifactSizeMismatch",
                "Artifact content ended before its declared size."));
        }

        if (request.ExpectedSha256 is { } expectedSha256
            && !string.Equals(digest.Sha256, expectedSha256, StringComparison.Ordinal))
        {
            return Result.Failure<ArtifactDigest>(ApplicationError.Validation(
                "Traceability.ArtifactSha256Mismatch",
                "Artifact content does not match its declared SHA-256 hash."));
        }

        return Result.Success(digest);
    }

    private async Task<Result<StoredTraceArtifact>> MatchExistingAsync(
        StoreTraceArtifactRequest request,
        string storageKey,
        string existingPath,
        ArtifactDigest incoming,
        CancellationToken cancellationToken)
    {
        EnsureSafeDirectory(Path.GetDirectoryName(existingPath)!);
        RejectReparseTarget(existingPath);
        await using var existing = new FileStream(
            existingPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var existingSha256 = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false));
        if (existing.Length != incoming.SizeBytes
            || !string.Equals(existingSha256, incoming.Sha256, StringComparison.Ordinal))
        {
            return Result.Failure<StoredTraceArtifact>(ApplicationError.Conflict(
                "Traceability.ArtifactStorageKeyConflict",
                $"Artifact storage key {storageKey} already contains different immutable content."));
        }

        var attributes = File.GetAttributes(existingPath);
        if (!attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(existingPath, attributes | FileAttributes.ReadOnly);
        }

        return Result.Success(ToStored(request, storageKey, incoming));
    }

    private static StoredTraceArtifact ToStored(
        StoreTraceArtifactRequest request,
        string storageKey,
        ArtifactDigest digest) => new(
        storageKey,
        ValidateFileName(request.FileName),
        ValidateOptional(request.MediaType),
        digest.SizeBytes,
        digest.Sha256);

    public async Task<Result<TraceArtifactContent>> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return Result.Failure<TraceArtifactContent>(ApplicationError.Validation(
                "Traceability.ArtifactStorageKeyRequired",
                "Artifact storage key is required."));
        }

        string canonicalStorageKey;
        try
        {
            canonicalStorageKey = ValidateStorageKey(storageKey);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<TraceArtifactContent>(ApplicationError.Validation(
                "Traceability.ArtifactStorageKeyInvalid",
                exception.Message));
        }

        var resolvedPath = ResolveArtifactPath(canonicalStorageKey);
        if (resolvedPath.IsFailure)
        {
            return Result.Failure<TraceArtifactContent>(resolvedPath.Error);
        }

        if (!File.Exists(resolvedPath.Value))
        {
            return Result.Failure<TraceArtifactContent>(ApplicationError.NotFound(
                "Traceability.ArtifactNotFound",
                $"Artifact {canonicalStorageKey} was not found."));
        }


        try
        {
            EnsureSafeDirectory(Path.GetDirectoryName(resolvedPath.Value)!);
            RejectReparseTarget(resolvedPath.Value);
        }
        catch (Exception exception) when (exception is IOException
                                            or UnauthorizedAccessException
                                            or InvalidDataException)
        {
            return Result.Failure<TraceArtifactContent>(ApplicationError.Validation(
                "Traceability.ArtifactPathUnsafe",
                exception.Message));
        }

        var stream = new FileStream(
            resolvedPath.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fileInfo = new FileInfo(resolvedPath.Value);
        byte[] hash;
        try
        {
            hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        stream.Position = 0;

        return Result.Success(new TraceArtifactContent(
            canonicalStorageKey,
            Path.GetFileName(resolvedPath.Value),
            InferMediaType(resolvedPath.Value),
            fileInfo.Length,
            Convert.ToHexString(hash).ToLowerInvariant(),
            stream));
    }

    private Result<string> ResolveArtifactPath(string storageKey)
    {
        var relativePath = storageKey.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        var rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, _pathComparison))
        {
            return Result.Failure<string>(ApplicationError.Validation(
                "Traceability.ArtifactStorageKeyInvalid",
                "Artifact storage key must resolve under the configured artifact storage root."));
        }

        return Result.Success(fullPath);
    }

    private static ApplicationError? ValidateStoreRequest(
        StoreTraceArtifactRequest request,
        long maximumArtifactSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactFileNameRequired",
                "Artifact file name is required.");
        }

        if (!IsCanonicalText(request.FileName))
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactFileNameNotCanonical",
                "Artifact file name must not contain leading or trailing whitespace.");
        }

        if (request.MediaType is not null && !IsCanonicalText(request.MediaType))
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactMediaTypeNotCanonical",
                "Artifact media type must be null or a non-empty canonical string.");
        }

        if (request.Content is null)
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactContentRequired",
                "Artifact content is required.");
        }

        if (request.ExpectedSha256 is not null
            && !IsCanonicalSha256(request.ExpectedSha256))
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactSha256Invalid",
                "ExpectedSha256 must be a lowercase 64-character SHA-256 value.");
        }

        if (request.ExpectedSizeBytes is < 0)
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactSizeInvalid",
                "ExpectedSizeBytes must be zero or greater.");
        }


        if (request.ExpectedSizeBytes > maximumArtifactSizeBytes)
        {
            return ApplicationError.Validation(
                "Traceability.ArtifactSizeLimitExceeded",
                $"ExpectedSizeBytes exceeds the configured {maximumArtifactSizeBytes}-byte limit.");
        }

        return null;
    }

    private static bool IsCanonicalSha256(string value)
    {
        return value.Length == 64
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string GenerateStorageKey(string fileName)
    {
        var safeFileName = ValidateFileName(fileName);
        var extension = Path.GetExtension(safeFileName);
        var generatedName = string.IsNullOrWhiteSpace(extension)
            ? Guid.NewGuid().ToString("N")
            : $"{Guid.NewGuid():N}{extension}";

        return $"{DateTimeOffset.UtcNow:yyyy/MM/dd}/{generatedName}";
    }

    private static string ValidateStorageKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Artifact storage key is required.", nameof(storageKey));
        }

        if (storageKey.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Artifact storage key must use '/' as the path separator.", nameof(storageKey));
        }

        if (!IsCanonicalText(storageKey))
        {
            throw new ArgumentException(
                "Artifact storage key must not contain leading or trailing whitespace.",
                nameof(storageKey));
        }

        var segments = storageKey.Split('/');
        if (segments.Length == 0
            || segments.Any(segment => segment.Length == 0
                || segment is "." or ".."
                || !IsCanonicalText(segment)))
        {
            throw new ArgumentException("Artifact storage key cannot contain empty or relative path segments.", nameof(storageKey));
        }

        if (segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("Artifact storage key contains invalid file name characters.", nameof(storageKey));
        }


        if (segments.Any(IsReservedPathSegment))
        {
            throw new ArgumentException(
                "Artifact storage key contains a reserved filesystem path segment.",
                nameof(storageKey));
        }

        return string.Join('/', segments);
    }

    private static string ValidateFileName(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        return string.IsNullOrWhiteSpace(safeFileName)
            ? throw new ArgumentException("Artifact file name is required.", nameof(fileName))
            : safeFileName;
    }

    private static string? ValidateOptional(string? value)
    {
        return value is null
            ? null
            : IsCanonicalText(value)
                ? value
                : throw new ArgumentException(
                    "Optional artifact text must be null or a non-empty canonical string.",
                    nameof(value));
    }

    private static bool IsCanonicalText(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[^1]);
    }


    private static bool IsReservedPathSegment(string segment)
    {
        if (segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            return true;
        }

        var deviceName = segment.Split('.', 2)[0];
        return deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || IsNumberedDevice(deviceName, "COM")
            || IsNumberedDevice(deviceName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == prefix.Length + 1
        && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && value[^1] is >= '1' and <= '9';

    private void EnsureSafeDirectory(string directoryPath)
    {
        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        var rootPrefix = _rootPath + Path.DirectorySeparatorChar;
        if (!string.Equals(fullDirectoryPath, _rootPath, _pathComparison)
            && !fullDirectoryPath.StartsWith(rootPrefix, _pathComparison))
        {
            throw new InvalidDataException(
                $"Artifact directory '{fullDirectoryPath}' escapes the configured storage root.");
        }

        RejectReparsePointsFromVolumeRoot(_rootPath);
        var relativePath = Path.GetRelativePath(_rootPath, fullDirectoryPath);
        var current = _rootPath;
        if (!string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            foreach (var segment in relativePath.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                {
                    Directory.CreateDirectory(current);
                }

                RejectDirectoryReparsePoint(current);
            }
        }

        RejectReparsePointsFromVolumeRoot(fullDirectoryPath);
    }

    private static void RejectReparsePointsFromVolumeRoot(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (!Directory.Exists(current))
            {
                throw new DirectoryNotFoundException(
                    $"Artifact directory '{current}' does not exist.");
            }

            RejectDirectoryReparsePoint(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null)
            {
                return;
            }

            current = parent;
        }
    }

    private static void RejectDirectoryReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Artifact path '{path}' is not a regular directory or contains a reparse point.");
        }
    }

    private static void RejectReparseTarget(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Artifact target '{path}' is not a regular file.");
        }
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

    private sealed record ArtifactDigest(long SizeBytes, string Sha256);
}
