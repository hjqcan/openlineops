using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.ReleaseManifest;

public static class ReleaseManifestVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static ReleaseManifestVerificationResult Verify(ReleaseManifestVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var document = LoadManifest(options.ManifestPath);
        if (document.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported release manifest schemaVersion {document.SchemaVersion}. Expected 1.");
        }

        VerifyManifestMetadata(document);
        if (document.Artifacts is null || document.Artifacts.Count == 0)
        {
            throw new InvalidOperationException("Release manifest does not contain any artifacts.");
        }

        VerifyArtifacts(options.ArtifactsDirectory, document.Artifacts);
        VerifyRequiredArtifactKinds(options, document.Artifacts);
        if (options.ChecksumsPath is not null)
        {
            VerifyChecksumsFile(options.ChecksumsPath, document.Artifacts);
        }

        return new ReleaseManifestVerificationResult(document.Artifacts.Count);
    }

    private static void VerifyManifestMetadata(ReleaseManifestDocument document)
    {
        if (!string.Equals(document.Product, ReleaseManifestContract.Product, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release product must be exactly '{ReleaseManifestContract.Product}'.");
        }

        ReleaseManifestContract.ValidateVersion(document.Version);
        ReleaseManifestContract.ValidateGeneratedAtUtc(document.GeneratedAtUtc);
        ReleaseManifestContract.ValidateCommit(document.Commit);
    }

    private static void ValidateOptions(ReleaseManifestVerificationOptions options)
    {
        if (!Directory.Exists(options.ArtifactsDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Artifacts directory '{options.ArtifactsDirectory}' does not exist.");
        }

        if (!File.Exists(options.ManifestPath))
        {
            throw new FileNotFoundException(
                $"Release manifest '{options.ManifestPath}' does not exist.",
                options.ManifestPath);
        }

        if (options.ChecksumsPath is not null && !File.Exists(options.ChecksumsPath))
        {
            throw new FileNotFoundException(
                $"Checksums file '{options.ChecksumsPath}' does not exist.",
                options.ChecksumsPath);
        }
    }

    private static ReleaseManifestDocument LoadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<ReleaseManifestDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Release manifest '{manifestPath}' could not be read.");
    }

    private static void VerifyRequiredArtifactKinds(
        ReleaseManifestVerificationOptions options,
        IReadOnlyCollection<ReleaseArtifactEntry> artifacts)
    {
        var requiredKinds = options.RequiredArtifactKinds
            .Select(ReleaseArtifactKinds.Parse)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requiredKinds.Length == 0)
        {
            return;
        }

        var availableKinds = artifacts
            .Select(artifact => artifact.Kind)
            .ToHashSet(StringComparer.Ordinal);
        var missingKinds = requiredKinds
            .Where(kind => !availableKinds.Contains(kind))
            .ToArray();
        if (missingKinds.Length == 0)
        {
            return;
        }

        var available = string.Join(
            ", ",
            availableKinds.OrderBy(kind => kind, StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"Required artifact kind(s) were missing: {string.Join(", ", missingKinds)}. "
            + $"Available artifact kind(s): {available}.");
    }

    private static void VerifyArtifacts(
        string artifactsDirectory,
        IReadOnlyCollection<ReleaseArtifactEntry> artifacts)
    {
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            if (artifact is null)
            {
                throw new InvalidOperationException("Release manifest contains a null artifact entry.");
            }

            VerifyArtifactMetadata(artifact);
            if (!relativePaths.Add(artifact.RelativePath))
            {
                throw new InvalidOperationException(
                    $"Release manifest contains duplicate artifact path '{artifact.RelativePath}'.");
            }

            var artifactPath = ResolveArtifactPath(artifactsDirectory, artifact.RelativePath);
            if (!File.Exists(artifactPath))
            {
                throw new FileNotFoundException(
                    $"Release artifact '{artifact.RelativePath}' does not exist.",
                    artifactPath);
            }

            var fileInfo = new FileInfo(artifactPath);
            if (fileInfo.Length != artifact.SizeBytes)
            {
                throw new InvalidOperationException(
                    $"Release artifact '{artifact.RelativePath}' size mismatch. "
                    + $"Expected {artifact.SizeBytes}, found {fileInfo.Length}.");
            }

            var sha256 = ComputeSha256(artifactPath);
            if (!string.Equals(sha256, artifact.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Release artifact '{artifact.RelativePath}' SHA-256 mismatch.");
            }

            var pathKind = ReleaseArtifactKinds.FromRelativePath(artifact.RelativePath);
            if (!string.Equals(pathKind, artifact.Kind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Release artifact '{artifact.RelativePath}' kind mismatch. "
                    + $"Expected '{pathKind}', found '{artifact.Kind}'.");
            }
        }
    }

    private static void VerifyArtifactMetadata(ReleaseArtifactEntry artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.RelativePath))
        {
            throw new InvalidOperationException("Release manifest contains an artifact with an empty relative path.");
        }

        if (artifact.RelativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' must use forward slashes in the manifest.");
        }

        var pathSegments = artifact.RelativePath.Split('/');
        if (Path.IsPathRooted(artifact.RelativePath)
            || pathSegments.Any(segment => !IsCanonicalPathSegment(segment)))
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' is not a canonical relative path.");
        }

        if (string.IsNullOrWhiteSpace(artifact.FileName))
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' has an empty file name.");
        }

        if (!string.Equals(
                artifact.FileName,
                Path.GetFileName(artifact.RelativePath),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' file name mismatch.");
        }

        if (string.IsNullOrWhiteSpace(artifact.Kind))
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' has an empty artifact kind.");
        }

        ReleaseArtifactKinds.Parse(artifact.Kind);

        if (artifact.SizeBytes < 0)
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' has a negative size.");
        }

        if (!IsCanonicalSha256(artifact.Sha256))
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' must use a lowercase 64-character SHA-256 value.");
        }
    }

    private static void VerifyChecksumsFile(
        string checksumsPath,
        IReadOnlyCollection<ReleaseArtifactEntry> artifacts)
    {
        var expected = artifacts.ToDictionary(
            artifact => artifact.RelativePath,
            artifact => artifact.Sha256,
            StringComparer.Ordinal);
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in File.ReadAllLines(checksumsPath))
        {
            if (rawLine.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Invalid empty checksum line in '{checksumsPath}'.");
            }

            if (rawLine.Length <= 66
                || rawLine[64] != ' '
                || rawLine[65] != ' ')
            {
                throw new InvalidOperationException(
                    $"Invalid checksum line in '{checksumsPath}': {rawLine}");
            }

            var hash = rawLine[..64];
            var relativePath = rawLine[66..];
            if (!IsCanonicalSha256(hash)
                || relativePath.Length == 0
                || !string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal)
                || relativePath.Contains('\\', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invalid checksum line in '{checksumsPath}': {rawLine}");
            }

            if (!actual.TryAdd(relativePath, hash))
            {
                throw new InvalidOperationException(
                    $"Checksums file contains duplicate artifact path '{relativePath}'.");
            }
        }

        foreach (var (relativePath, expectedHash) in expected)
        {
            if (!actual.TryGetValue(relativePath, out var actualHash))
            {
                throw new InvalidOperationException(
                    $"Checksums file is missing artifact '{relativePath}'.");
            }

            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Checksums file SHA-256 mismatch for artifact '{relativePath}'.");
            }
        }

        var extraPaths = actual.Keys
            .Where(path => !expected.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (extraPaths.Length > 0)
        {
            throw new InvalidOperationException(
                $"Checksums file contains artifact(s) not present in the release manifest: "
                + string.Join(", ", extraPaths));
        }
    }

    private static string ResolveArtifactPath(string artifactsDirectory, string relativePath)
    {
        var fullRoot = Path
            .GetFullPath(artifactsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        var requestedPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!requestedPath.StartsWith(rootPrefix, pathComparison))
        {
            throw new InvalidOperationException(
                $"Release artifact '{relativePath}' resolves outside the artifacts directory.");
        }

        var currentPath = fullRoot;
        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var exactMatches = Directory
                .EnumerateFileSystemEntries(currentPath)
                .Where(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.Ordinal))
                .ToArray();
            if (exactMatches.Length != 1)
            {
                throw new FileNotFoundException(
                    $"Release artifact '{relativePath}' does not exist with its exact canonical path.",
                    requestedPath);
            }

            currentPath = exactMatches[0];
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Release artifact '{relativePath}' must not traverse a symbolic link or junction.");
            }

            if (index < segments.Length - 1 && !Directory.Exists(currentPath))
            {
                throw new InvalidOperationException(
                    $"Release artifact '{relativePath}' contains a non-directory path segment '{segment}'.");
            }
        }

        return currentPath;
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsCanonicalSha256(string? value)
    {
        return value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsCanonicalPathSegment(string segment)
    {
        return segment.Length > 0
            && segment is not "." and not ".."
            && string.Equals(segment, segment.Trim(), StringComparison.Ordinal)
            && segment.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-');
    }

}
