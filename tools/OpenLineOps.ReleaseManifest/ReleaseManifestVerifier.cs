using System.Security.Cryptography;
using System.Text.Json;

namespace OpenLineOps.ReleaseManifest;

public static class ReleaseManifestVerifier
{
    private const string OtherArtifactKind = "other";

    private static readonly (string Alias, string Kind)[] ArtifactKindAliases =
    [
        ("plugin-host", "plugin-host"),
        ("pluginhost", "plugin-host"),
        ("script-worker", "script-worker"),
        ("scriptworker", "script-worker"),
        ("sample-plugins", "sample-plugin"),
        ("sample-plugin", "sample-plugin"),
        ("desktop", "desktop"),
        ("electron", "desktop"),
        ("source", "source"),
        ("api", "api")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
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

        if (document.Artifacts.Count == 0)
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
            .Select(NormalizeArtifactKind)
            .Where(kind => kind.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requiredKinds.Length == 0)
        {
            return;
        }

        var availableKinds = artifacts
            .Select(artifact => artifact.Kind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingKinds = requiredKinds
            .Where(kind => !availableKinds.Contains(kind))
            .ToArray();
        if (missingKinds.Length == 0)
        {
            return;
        }

        var available = string.Join(
            ", ",
            availableKinds.OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase));
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
            if (!string.Equals(sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Release artifact '{artifact.RelativePath}' SHA-256 mismatch.");
            }

            var inferredKind = InferArtifactKind(artifact.RelativePath);
            if (!string.Equals(inferredKind, artifact.Kind, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Release artifact '{artifact.RelativePath}' kind mismatch. "
                    + $"Expected '{inferredKind}', found '{artifact.Kind}'.");
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

        if (artifact.SizeBytes < 0)
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' has a negative size.");
        }

        if (string.IsNullOrWhiteSpace(artifact.Sha256)
            || artifact.Sha256.Length != 64
            || !artifact.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"Release artifact '{artifact.RelativePath}' has an invalid SHA-256 value.");
        }
    }

    private static void VerifyChecksumsFile(
        string checksumsPath,
        IReadOnlyCollection<ReleaseArtifactEntry> artifacts)
    {
        var expected = artifacts.ToDictionary(
            artifact => artifact.RelativePath,
            artifact => artifact.Sha256,
            StringComparer.OrdinalIgnoreCase);
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadAllLines(checksumsPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOfAny([' ', '\t']);
            if (separatorIndex <= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid checksum line in '{checksumsPath}': {rawLine}");
            }

            var hash = line[..separatorIndex].Trim();
            var relativePath = line[separatorIndex..].Trim();
            if (relativePath.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Invalid checksum line in '{checksumsPath}': {rawLine}");
            }

            relativePath = relativePath.Replace('\\', '/');
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

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Checksums file SHA-256 mismatch for artifact '{relativePath}'.");
            }
        }

        var extraPaths = actual.Keys
            .Where(path => !expected.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
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
        var artifactPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!artifactPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Release artifact '{relativePath}' resolves outside the artifacts directory.");
        }

        return artifactPath;
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string InferArtifactKind(string relativePath)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var separatorIndex = normalizedPath.IndexOf('/', StringComparison.Ordinal);
        var topLevelToken = separatorIndex > 0
            ? normalizedPath[..separatorIndex]
            : Path.GetFileNameWithoutExtension(normalizedPath);

        return MatchArtifactKindAlias(topLevelToken, allowPrefix: true);
    }

    private static string NormalizeArtifactKind(string value)
    {
        return MatchArtifactKindAlias(value, allowPrefix: false);
    }

    private static string MatchArtifactKindAlias(string value, bool allowPrefix)
    {
        var token = value.Trim().ToLowerInvariant();
        if (token.Length == 0)
        {
            return string.Empty;
        }

        foreach (var (alias, kind) in ArtifactKindAliases)
        {
            if (string.Equals(token, alias, StringComparison.OrdinalIgnoreCase)
                || allowPrefix && token.StartsWith(alias + "-", StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return allowPrefix ? OtherArtifactKind : token;
    }
}
