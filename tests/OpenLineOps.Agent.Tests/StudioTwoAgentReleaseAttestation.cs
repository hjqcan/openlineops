using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenLineOps.Agent.Tests;

public sealed partial class StagedAgentRabbitMqProcessE2ETests
{
    private const string StudioReleaseManifestVariable =
        "OPENLINEOPS_STUDIO_TWO_AGENT_RELEASE_MANIFEST_PATH";

    private sealed record StudioReleaseArtifactAttestation(
        string Kind,
        long ArchiveSizeBytes,
        string ArchiveSha256,
        int BundleFileCount,
        string BundleContentSha256,
        string EntrypointSha256);

    private sealed record StudioReleaseAttestation(
        string Version,
        string ManifestSha256,
        StudioReleaseArtifactAttestation Agent,
        StudioReleaseArtifactAttestation Api,
        StudioReleaseArtifactAttestation SamplePlugin);

    private static StudioReleaseAttestation LoadStudioReleaseAttestation(
        string manifestPath,
        StagedPrerequisites prerequisites)
    {
        var canonicalManifest = StudioRequiredCanonicalFile(
            manifestPath,
            StudioReleaseManifestVariable);
        if (!string.Equals(
                Path.GetFileName(canonicalManifest),
                "release-manifest.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{StudioReleaseManifestVariable} must identify release-manifest.json.");
        }

        using var manifest = StudioReadJson(canonicalManifest, "release manifest");
        var root = manifest.RootElement;
        StudioRequireExactProperties(
            root,
            "release manifest",
            "schemaVersion",
            "product",
            "version",
            "generatedAtUtc",
            "commit",
            "artifacts");
        if (StudioRequiredProperty(root, "schemaVersion").ValueKind != JsonValueKind.Number
            || !StudioRequiredProperty(root, "schemaVersion").TryGetInt32(out var schemaVersion)
            || schemaVersion != 1)
        {
            throw new InvalidDataException("Release manifest schemaVersion must be 1.");
        }

        StudioRequireExactString(root, "product", "OpenLineOps");
        var version = StudioRequiredString(root, "version");
        _ = StudioRequiredUtcTimestamp(root, "generatedAtUtc");
        if (StudioRequiredProperty(root, "commit").ValueKind is not (
                JsonValueKind.Null or JsonValueKind.String))
        {
            throw new InvalidDataException("Release manifest commit must be null or text.");
        }

        var artifacts = StudioRequiredArray(root, "artifacts")
            .EnumerateArray()
            .Select((element, index) => ReadStudioReleaseArtifact(
                element,
                index,
                Path.GetDirectoryName(canonicalManifest)!))
            .ToArray();
        if (artifacts.Select(static artifact => artifact.Kind)
            .Distinct(StringComparer.Ordinal).Count() != artifacts.Length)
        {
            throw new InvalidDataException("Release manifest artifact kinds must be unique.");
        }

        var agent = AttestStudioReleaseBundle(
            RequireStudioReleaseKind(artifacts, "agent"),
            prerequisites.AgentBundleRoot,
            "OpenLineOps.Agent.exe");
        var api = AttestStudioReleaseBundle(
            RequireStudioReleaseKind(artifacts, "api"),
            prerequisites.ApiBundleRoot,
            "OpenLineOps.Api.exe");
        var samplePlugin = AttestStudioReleaseBundle(
            RequireStudioReleaseKind(artifacts, "sample-plugin"),
            prerequisites.SamplePluginRoot,
            "OpenLineOps.SamplePlugins.LoopbackDevice.dll");
        return new StudioReleaseAttestation(
            version,
            StudioSha256File(canonicalManifest),
            agent,
            api,
            samplePlugin);
    }

    private sealed record StudioReleaseManifestArtifact(
        string Kind,
        string ArchivePath,
        long SizeBytes,
        string Sha256);

    private static StudioReleaseManifestArtifact ReadStudioReleaseArtifact(
        JsonElement element,
        int index,
        string releaseRoot)
    {
        var label = $"release manifest artifacts[{index}]";
        StudioRequireExactProperties(
            element,
            label,
            "relativePath",
            "fileName",
            "kind",
            "sizeBytes",
            "sha256");
        var relativePath = StudioRequiredRelativePath(element, "relativePath");
        var fileName = StudioRequiredString(element, "fileName");
        if (!string.Equals(
                Path.GetFileName(relativePath),
                fileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} fileName differs from relativePath.");
        }

        var archivePath = ResolveStudioPathUnderRoot(releaseRoot, relativePath, label);
        var sizeBytes = StudioRequiredInt64(element, "sizeBytes");
        var sha256 = StudioRequiredSha256(element, "sha256");
        StudioVerifyFileIdentity(archivePath, sizeBytes, sha256, label);
        return new StudioReleaseManifestArtifact(
            StudioRequiredString(element, "kind"),
            archivePath,
            sizeBytes,
            sha256);
    }

    private static StudioReleaseManifestArtifact RequireStudioReleaseKind(
        IReadOnlyCollection<StudioReleaseManifestArtifact> artifacts,
        string kind) => artifacts.SingleOrDefault(artifact => string.Equals(
            artifact.Kind,
            kind,
            StringComparison.Ordinal))
        ?? throw new InvalidDataException(
            $"Release manifest is missing the required '{kind}' artifact.");

    private static StudioReleaseArtifactAttestation AttestStudioReleaseBundle(
        StudioReleaseManifestArtifact artifact,
        string bundleRoot,
        string entrypointFileName)
    {
        var canonicalBundleRoot = RequiredDirectory(bundleRoot, $"{artifact.Kind} bundle root");
        var bundleFiles = EnumerateStudioBundleFiles(canonicalBundleRoot);
        using var archive = ZipFile.OpenRead(artifact.ArchivePath);
        var archiveEntries = archive.Entries
            .Where(static entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();
        if (archiveEntries.Length != bundleFiles.Count)
        {
            throw new InvalidDataException(
                $"The extracted {artifact.Kind} bundle file count differs from its release archive.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archiveEntries)
        {
            var relativePath = RequireStudioZipEntryPath(entry.FullName, artifact.Kind);
            if (!seen.Add(relativePath)
                || !bundleFiles.TryGetValue(relativePath, out var extractedPath)
                || new FileInfo(extractedPath).Length != entry.Length)
            {
                throw new InvalidDataException(
                    $"Release archive entry '{relativePath}' differs from the extracted {artifact.Kind} bundle.");
            }

            using var archiveStream = entry.Open();
            using var extractedStream = new FileStream(
                extractedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            var archiveHash = Convert.ToHexStringLower(SHA256.HashData(archiveStream));
            var extractedHash = Convert.ToHexStringLower(SHA256.HashData(extractedStream));
            if (!string.Equals(archiveHash, extractedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Release archive entry '{relativePath}' changed after extraction.");
            }
        }

        var inventory = new StringBuilder();
        foreach (var (relativePath, filePath) in bundleFiles.OrderBy(
                     static item => item.Key,
                     StringComparer.Ordinal))
        {
            var file = new FileInfo(filePath);
            inventory.Append(relativePath)
                .Append('\0')
                .Append(file.Length)
                .Append('\0')
                .Append(StudioSha256File(filePath))
                .Append('\n');
        }

        var entrypointPath = RequiredDirectFile(canonicalBundleRoot, entrypointFileName);
        return new StudioReleaseArtifactAttestation(
            artifact.Kind,
            artifact.SizeBytes,
            artifact.Sha256,
            bundleFiles.Count,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                inventory.ToString()))),
            StudioSha256File(entrypointPath));
    }

    private static Dictionary<string, string> EnumerateStudioBundleFiles(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            RejectStudioReparsePoint(directory, "staged release bundle");
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                RejectStudioReparsePoint(item, "staged release bundle");
                if (item is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, item.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!result.TryAdd(relativePath, item.FullName))
                {
                    throw new InvalidDataException(
                        $"Staged release bundle contains a duplicate path '{relativePath}'.");
                }
            }
        }

        return result;
    }

    private static string RequireStudioZipEntryPath(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(value)
            || value.Split('/').Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"The {kind} release archive contains an unsafe entry path.");
        }

        return value;
    }

    private static string ResolveStudioPathUnderRoot(
        string root,
        string relativePath,
        string label)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(
            relativePath.Replace('/', Path.DirectorySeparatorChar),
            canonicalRoot);
        if (!path.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} escapes the release root.");
        }

        return path;
    }

    private static void RejectStudioReparsePoint(FileSystemInfo item, string label)
    {
        item.Refresh();
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} cannot contain reparse points.");
        }
    }
}
