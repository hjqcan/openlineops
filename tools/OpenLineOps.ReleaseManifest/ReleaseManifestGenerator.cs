using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenLineOps.ReleaseManifest;

public static class ReleaseManifestGenerator
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static ReleaseManifestDocument Generate(ReleaseManifestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var artifactFiles = FindArtifactFiles(options).ToArray();
        if (artifactFiles.Length == 0)
        {
            throw new InvalidOperationException(
                $"No release artifacts were found under '{options.ArtifactsDirectory}'.");
        }

        var artifacts = artifactFiles
            .Select(file => CreateArtifactEntry(options.ArtifactsDirectory, file))
            .ToArray();
        ValidateRequiredArtifactKinds(options, artifacts);

        var document = new ReleaseManifestDocument(
            SchemaVersion: 2,
            Product: options.Product.Trim(),
            Version: options.Version.Trim(),
            GeneratedAtUtc: options.GeneratedAtUtc.ToUniversalTime().ToString("O"),
            Commit: string.IsNullOrWhiteSpace(options.Commit) ? null : options.Commit.Trim(),
            Artifacts: artifacts);

        WriteTextFile(
            options.ManifestPath,
            JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine);
        WriteTextFile(
            options.ChecksumsPath,
            BuildChecksumsFile(artifacts));
        if (options.NotesPath is not null)
        {
            WriteTextFile(
                options.NotesPath,
                BuildReleaseNotes(document));
        }

        return document;
    }

    private static void ValidateOptions(ReleaseManifestOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Product))
        {
            throw new ArgumentException("Product is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Version))
        {
            throw new ArgumentException("Version is required.", nameof(options));
        }

        if (!Directory.Exists(options.ArtifactsDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Artifacts directory '{options.ArtifactsDirectory}' does not exist.");
        }
    }

    private static IEnumerable<string> FindArtifactFiles(ReleaseManifestOptions options)
    {
        var excludedOutputs = new HashSet<string>(
            [
                NormalizePath(options.ManifestPath),
                NormalizePath(options.ChecksumsPath),
                options.NotesPath is null ? string.Empty : NormalizePath(options.NotesPath)
            ],
            StringComparer.OrdinalIgnoreCase);

        return Directory
            .EnumerateFiles(options.ArtifactsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !excludedOutputs.Contains(NormalizePath(path)))
            .OrderBy(path => ToRelativePath(options.ArtifactsDirectory, path), StringComparer.Ordinal);
    }

    private static ReleaseArtifactEntry CreateArtifactEntry(
        string artifactsDirectory,
        string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var relativePath = ToRelativePath(artifactsDirectory, filePath);

        return new ReleaseArtifactEntry(
            RelativePath: relativePath,
            FileName: fileInfo.Name,
            Kind: InferArtifactKind(relativePath),
            SizeBytes: fileInfo.Length,
            Sha256: ComputeSha256(filePath));
    }

    private static void ValidateRequiredArtifactKinds(
        ReleaseManifestOptions options,
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

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildChecksumsFile(IReadOnlyCollection<ReleaseArtifactEntry> artifacts)
    {
        var builder = new StringBuilder();
        foreach (var artifact in artifacts)
        {
            builder
                .Append(artifact.Sha256)
                .Append("  ")
                .Append(artifact.RelativePath)
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string BuildReleaseNotes(ReleaseManifestDocument document)
    {
        var builder = new StringBuilder()
            .Append("# ")
            .Append(document.Product)
            .Append(' ')
            .Append(document.Version)
            .AppendLine()
            .AppendLine()
            .Append("Generated: ")
            .Append(document.GeneratedAtUtc)
            .AppendLine()
            .Append("Commit: ")
            .AppendLine(string.IsNullOrWhiteSpace(document.Commit) ? "not provided" : document.Commit)
            .AppendLine()
            .AppendLine("## Artifacts")
            .AppendLine()
            .AppendLine("| Artifact | Kind | Size (bytes) | SHA-256 |")
            .AppendLine("| --- | --- | ---: | --- |");

        foreach (var artifact in document.Artifacts)
        {
            builder
                .Append("| `")
                .Append(artifact.RelativePath)
                .Append("` | ")
                .Append(artifact.Kind)
                .Append(" | ")
                .Append(artifact.SizeBytes)
                .Append(" | `")
                .Append(artifact.Sha256)
                .AppendLine("` |");
        }

        return builder
            .AppendLine()
            .AppendLine("## Migration Notes")
            .AppendLine()
            .AppendLine("- Review persistence migrations, plugin contract changes, and desktop packaging notes before publishing.")
            .ToString();
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

    private static void WriteTextFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static string ToRelativePath(string root, string path)
    {
        return Path
            .GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }
}
