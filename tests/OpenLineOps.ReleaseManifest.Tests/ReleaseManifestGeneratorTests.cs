using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.ReleaseManifest;

namespace OpenLineOps.ReleaseManifest.Tests;

public sealed class ReleaseManifestGeneratorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "OpenLineOps.ReleaseManifest.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GenerateWritesManifestChecksumsAndReleaseNotes()
    {
        var artifacts = Path.Combine(_directory, "artifacts");
        Directory.CreateDirectory(Path.Combine(artifacts, "desktop"));
        File.WriteAllText(Path.Combine(artifacts, "api.zip"), "api package");
        File.WriteAllText(Path.Combine(artifacts, "desktop", "desktop.zip"), "desktop package");
        File.WriteAllText(Path.Combine(artifacts, "release-manifest.json"), "stale manifest");
        File.WriteAllText(Path.Combine(artifacts, "checksums.sha256"), "stale checksums");
        File.WriteAllText(Path.Combine(artifacts, "release-notes.md"), "stale notes");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        var notesPath = Path.Combine(artifacts, "release-notes.md");

        var document = ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Product: "OpenLineOps",
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: notesPath,
            Commit: "abc123",
            GeneratedAtUtc: new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: []));

        Assert.Equal(2, document.Artifacts.Count);
        Assert.Collection(
            document.Artifacts,
            artifact =>
            {
                Assert.Equal("api.zip", artifact.RelativePath);
                Assert.Equal("api", artifact.Kind);
                Assert.Equal(ExpectedSha256("api package"), artifact.Sha256);
            },
            artifact =>
            {
                Assert.Equal("desktop/desktop.zip", artifact.RelativePath);
                Assert.Equal("desktop", artifact.Kind);
                Assert.Equal(ExpectedSha256("desktop package"), artifact.Sha256);
            });

        var manifestJson = File.ReadAllText(manifestPath);
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("0.1.0", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal("abc123", manifest.RootElement.GetProperty("commit").GetString());
        Assert.Equal(2, manifest.RootElement.GetProperty("artifacts").GetArrayLength());
        Assert.Equal(
            "desktop",
            manifest.RootElement
                .GetProperty("artifacts")
                .EnumerateArray()
                .Last()
                .GetProperty("kind")
                .GetString());

        var checksums = File.ReadAllLines(checksumsPath);
        Assert.Equal(
            $"{ExpectedSha256("api package")}  api.zip",
            checksums[0]);
        Assert.Equal(
            $"{ExpectedSha256("desktop package")}  desktop/desktop.zip",
            checksums[1]);

        var notes = File.ReadAllText(notesPath);
        Assert.Contains("# OpenLineOps 0.1.0", notes, StringComparison.Ordinal);
        Assert.Contains("| `desktop/desktop.zip` | desktop |", notes, StringComparison.Ordinal);
        Assert.Contains("## Migration Notes", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateThrowsWhenRequiredArtifactKindIsMissing()
    {
        var artifacts = Path.Combine(_directory, "required-kinds");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "api.zip"), "api package");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
                Product: "OpenLineOps",
                Version: "0.1.0",
                ArtifactsDirectory: artifacts,
                ManifestPath: Path.Combine(_directory, "missing-kind-manifest.json"),
                ChecksumsPath: Path.Combine(_directory, "missing-kind-checksums.sha256"),
                NotesPath: null,
                Commit: null,
                GeneratedAtUtc: new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero),
                RequiredArtifactKinds: ["api", "desktop"])));

        Assert.Contains("Required artifact kind", exception.Message, StringComparison.Ordinal);
        Assert.Contains("desktop", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandAcceptsRepeatableRequiredArtifactKinds()
    {
        var artifacts = Path.Combine(_directory, "command-required-kinds");
        Directory.CreateDirectory(Path.Combine(artifacts, "desktop"));
        File.WriteAllText(Path.Combine(artifacts, "api-0.1.0.zip"), "api package");
        File.WriteAllText(Path.Combine(artifacts, "desktop", "desktop.zip"), "desktop package");
        var manifestPath = Path.Combine(_directory, "command-manifest.json");
        var checksumsPath = Path.Combine(_directory, "command-checksums.sha256");

        var exitCode = ReleaseManifestCommand.Run(
            [
                "--version", "0.1.0",
                "--artifacts", artifacts,
                "--output", manifestPath,
                "--checksums", checksumsPath,
                "--require-kind", "api",
                "--require-kind", "desktop"
            ],
            _directory);

        Assert.Equal(0, exitCode);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var artifactKinds = manifest.RootElement
            .GetProperty("artifacts")
            .EnumerateArray()
            .Select(artifact => artifact.GetProperty("kind").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(["api", "desktop"], artifactKinds);
    }

    [Fact]
    public void VerifySucceedsForGeneratedManifestAndChecksums()
    {
        var artifacts = Path.Combine(_directory, "verify-success");
        Directory.CreateDirectory(Path.Combine(artifacts, "desktop"));
        File.WriteAllText(Path.Combine(artifacts, "api-0.1.0.zip"), "api package");
        File.WriteAllText(Path.Combine(artifacts, "desktop", "desktop.zip"), "desktop package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");

        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Product: "OpenLineOps",
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api", "desktop"]));

        var result = ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            RequiredArtifactKinds: ["api", "desktop"]));

        Assert.Equal(2, result.ArtifactCount);
    }

    [Fact]
    public void VerifyThrowsWhenArtifactHashDoesNotMatchManifest()
    {
        var artifacts = Path.Combine(_directory, "verify-hash-mismatch");
        Directory.CreateDirectory(artifacts);
        var artifactPath = Path.Combine(artifacts, "api-0.1.0.zip");
        File.WriteAllText(artifactPath, "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Product: "OpenLineOps",
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api"]));
        File.WriteAllText(artifactPath, "api PACKAGE");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
                ArtifactsDirectory: artifacts,
                ManifestPath: manifestPath,
                ChecksumsPath: checksumsPath,
                RequiredArtifactKinds: ["api"])));

        Assert.Contains("SHA-256 mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandCanVerifyExistingManifest()
    {
        var artifacts = Path.Combine(_directory, "command-verify");
        Directory.CreateDirectory(Path.Combine(artifacts, "desktop"));
        File.WriteAllText(Path.Combine(artifacts, "api-0.1.0.zip"), "api package");
        File.WriteAllText(Path.Combine(artifacts, "desktop", "desktop.zip"), "desktop package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Product: "OpenLineOps",
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api", "desktop"]));

        var exitCode = ReleaseManifestCommand.Run(
            [
                "--verify",
                "--artifacts", artifacts,
                "--manifest", manifestPath,
                "--checksums", checksumsPath,
                "--require-kind", "api",
                "--require-kind", "desktop"
            ],
            _directory);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void GenerateThrowsWhenArtifactsDirectoryIsEmpty()
    {
        var artifacts = Path.Combine(_directory, "empty");
        Directory.CreateDirectory(artifacts);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
                Product: "OpenLineOps",
                Version: "0.1.0",
                ArtifactsDirectory: artifacts,
                ManifestPath: Path.Combine(_directory, "release-manifest.json"),
                ChecksumsPath: Path.Combine(_directory, "checksums.sha256"),
                NotesPath: null,
                Commit: null,
                GeneratedAtUtc: new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero),
                RequiredArtifactKinds: [])));

        Assert.Contains("No release artifacts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandReturnsUsageErrorWhenRequiredOptionsAreMissing()
    {
        var exitCode = ReleaseManifestCommand.Run(
            ["--artifacts", _directory],
            _directory);

        Assert.Equal(2, exitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string ExpectedSha256(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
