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
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        Directory.CreateDirectory(Path.Combine(artifacts, "desktop"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");
        File.WriteAllText(Path.Combine(artifacts, "desktop", "desktop.zip"), "desktop package");
        File.WriteAllText(Path.Combine(artifacts, "release-manifest.json"), "stale manifest");
        File.WriteAllText(Path.Combine(artifacts, "checksums.sha256"), "stale checksums");
        File.WriteAllText(Path.Combine(artifacts, "release-notes.md"), "stale notes");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        var notesPath = Path.Combine(artifacts, "release-notes.md");

        var document = ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: notesPath,
            Commit: new string('a', 40),
            GeneratedAtUtc: new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: []));

        Assert.Equal(2, document.Artifacts.Count);
        Assert.Collection(
            document.Artifacts,
            artifact =>
            {
                Assert.Equal("api/api.zip", artifact.RelativePath);
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
        Assert.False(File.ReadAllBytes(manifestPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("0.1.0", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(new string('a', 40), manifest.RootElement.GetProperty("commit").GetString());
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
            $"{ExpectedSha256("api package")}  api/api.zip",
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
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
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
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        Directory.CreateDirectory(Path.Combine(artifacts, "desktop"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api-0.1.0.zip"), "api package");
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
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        Directory.CreateDirectory(Path.Combine(artifacts, "desktop"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api-0.1.0.zip"), "api package");
        File.WriteAllText(Path.Combine(artifacts, "desktop", "desktop.zip"), "desktop package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");

        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
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
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        var artifactPath = Path.Combine(artifacts, "api", "api-0.1.0.zip");
        File.WriteAllText(artifactPath, "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
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
    public void VerifyRejectsNonCanonicalManifestArtifactKind()
    {
        var artifacts = Path.Combine(_directory, "verify-kind");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api"]));
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace(
                "\"kind\": \"api\"",
                "\"kind\": \"API\"",
                StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
                ArtifactsDirectory: artifacts,
                ManifestPath: manifestPath,
                ChecksumsPath: checksumsPath,
                RequiredArtifactKinds: ["api"])));

        Assert.Contains("Unsupported release artifact kind", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRejectsUppercaseManifestDigestAlias()
    {
        var artifacts = Path.Combine(_directory, "verify-uppercase-digest");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        var manifest = ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api"]));
        var digest = Assert.Single(manifest.Artifacts).Sha256;
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace(
                digest,
                digest.ToUpperInvariant(),
                StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
                ArtifactsDirectory: artifacts,
                ManifestPath: manifestPath,
                ChecksumsPath: checksumsPath,
                RequiredArtifactKinds: ["api"])));

        Assert.Contains("lowercase", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("uppercase-hash")]
    [InlineData("path-case")]
    [InlineData("backslash-path")]
    public void VerifyRejectsNonCanonicalChecksumAliases(string mutation)
    {
        var artifacts = Path.Combine(_directory, $"verify-checksum-{mutation}");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        var manifest = ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api"]));
        var artifact = Assert.Single(manifest.Artifacts);
        var mutatedLine = mutation switch
        {
            "uppercase-hash" => $"{artifact.Sha256.ToUpperInvariant()}  {artifact.RelativePath}",
            "path-case" => $"{artifact.Sha256}  API/api.zip",
            "backslash-path" => $"{artifact.Sha256}  api\\api.zip",
            _ => throw new InvalidOperationException($"Unsupported test mutation '{mutation}'.")
        };
        File.WriteAllText(checksumsPath, mutatedLine + Environment.NewLine);

        Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
                ArtifactsDirectory: artifacts,
                ManifestPath: manifestPath,
                ChecksumsPath: checksumsPath,
                RequiredArtifactKinds: ["api"])));
    }

    [Theory]
    [InlineData("SchemaVersion")]
    [InlineData("unexpectedField")]
    public void VerifyRejectsManifestPropertyAliasesAndUnknownFields(string replacement)
    {
        var artifacts = Path.Combine(_directory, $"verify-property-{replacement}");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api"]));
        var json = File.ReadAllText(manifestPath);
        json = replacement == "SchemaVersion"
            ? json.Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal)
            : "{\"unexpectedField\":true," + json[1..];
        File.WriteAllText(manifestPath, json);

        Assert.Throws<JsonException>(() =>
            ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
                ArtifactsDirectory: artifacts,
                ManifestPath: manifestPath,
                ChecksumsPath: checksumsPath,
                RequiredArtifactKinds: ["api"])));
    }

    [Fact]
    public void VerifyRejectsDuplicateManifestProperties()
    {
        var artifacts = Path.Combine(_directory, "verify-duplicate-property");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api"]));
        var json = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, "{\"schemaVersion\":1," + json[1..]);

        Assert.Throws<JsonException>(() =>
            ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
                ArtifactsDirectory: artifacts,
                ManifestPath: manifestPath,
                ChecksumsPath: checksumsPath,
                RequiredArtifactKinds: ["api"])));
    }

    [Theory]
    [InlineData("product")]
    [InlineData("version")]
    [InlineData("generatedAtUtc")]
    [InlineData("commit")]
    public void VerifyRejectsNonCanonicalManifestMetadata(string mutation)
    {
        var artifacts = Path.Combine(_directory, $"verify-metadata-{mutation}");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        var manifest = ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: new string('a', 40),
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api"]));
        var json = File.ReadAllText(manifestPath);
        json = mutation switch
        {
            "product" => json.Replace("\"OpenLineOps\"", "\"openlineops\"", StringComparison.Ordinal),
            "version" => json.Replace("\"0.1.0\"", "\"00.1.0\"", StringComparison.Ordinal),
            "generatedAtUtc" => json.Replace(
                JsonEncodedText.Encode(manifest.GeneratedAtUtc).ToString(),
                "2026-07-10T12:00:00Z",
                StringComparison.Ordinal),
            "commit" => json.Replace(new string('a', 40), new string('A', 40), StringComparison.Ordinal),
            _ => throw new InvalidOperationException($"Unsupported mutation '{mutation}'.")
        };
        File.WriteAllText(manifestPath, json);

        Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
                ArtifactsDirectory: artifacts,
                ManifestPath: manifestPath,
                ChecksumsPath: checksumsPath,
                RequiredArtifactKinds: ["api"])));
    }

    [Fact]
    public void VerifyRejectsArtifactPathCaseAliasOnCaseInsensitiveFileSystems()
    {
        var artifacts = Path.Combine(_directory, "verify-path-case-alias");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
            Version: "0.1.0",
            ArtifactsDirectory: artifacts,
            ManifestPath: manifestPath,
            ChecksumsPath: checksumsPath,
            NotesPath: null,
            Commit: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            RequiredArtifactKinds: ["api"]));
        var json = File.ReadAllText(manifestPath)
            .Replace("api/api.zip", "api/API.zip", StringComparison.Ordinal)
            .Replace("\"api.zip\"", "\"API.zip\"", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, json);

        Assert.Throws<FileNotFoundException>(() =>
            ReleaseManifestVerifier.Verify(new ReleaseManifestVerificationOptions(
                ArtifactsDirectory: artifacts,
                ManifestPath: manifestPath,
                ChecksumsPath: null,
                RequiredArtifactKinds: ["api"])));
    }

    [Theory]
    [InlineData(" 0.1.0", null)]
    [InlineData("01.0.0", null)]
    [InlineData("1.0.0-01", null)]
    [InlineData("0.1.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ")]
    [InlineData("0.1.0", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void GenerateRejectsNonCanonicalVersionOrCommit(string version, string? commit)
    {
        var artifacts = Path.Combine(_directory, $"generate-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");

        Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
                Version: version,
                ArtifactsDirectory: artifacts,
                ManifestPath: Path.Combine(artifacts, "release-manifest.json"),
                ChecksumsPath: Path.Combine(artifacts, "checksums.sha256"),
                NotesPath: null,
                Commit: commit,
                GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
                RequiredArtifactKinds: ["api"])));
    }

    [Fact]
    public void CommandCanVerifyExistingManifest()
    {
        var artifacts = Path.Combine(_directory, "command-verify");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        Directory.CreateDirectory(Path.Combine(artifacts, "desktop"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api-0.1.0.zip"), "api package");
        File.WriteAllText(Path.Combine(artifacts, "desktop", "desktop.zip"), "desktop package");
        var manifestPath = Path.Combine(artifacts, "release-manifest.json");
        var checksumsPath = Path.Combine(artifacts, "checksums.sha256");
        ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
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
    public void CommandVerifiesUniqueJsonPropertiesForMultipleDocuments()
    {
        Directory.CreateDirectory(_directory);
        var firstPath = Path.Combine(_directory, "unique-first.json");
        var secondPath = Path.Combine(_directory, "unique-second.json");
        File.WriteAllText(firstPath, "{\"schemaVersion\":1,\"nested\":{\"value\":true}}");
        File.WriteAllText(secondPath, "{\"items\":[{\"id\":\"one\"},{\"id\":\"two\"}]}");

        var exitCode = ReleaseManifestCommand.Run(
            ["--verify-json", firstPath, "--verify-json", secondPath],
            _directory);

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"schema\\u0056ersion\":1}")]
    public void JsonVerificationRejectsDuplicateLogicalPropertyNames(string json)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"duplicate-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        Assert.Throws<JsonException>(() => JsonPropertyUniquenessVerifier.VerifyFile(path));
    }

    [Fact]
    public void GenerateThrowsWhenArtifactsDirectoryIsEmpty()
    {
        var artifacts = Path.Combine(_directory, "empty");
        Directory.CreateDirectory(artifacts);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
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

    [Theory]
    [InlineData("electron")]
    [InlineData("pluginhost")]
    [InlineData("sample-plugins")]
    [InlineData("API")]
    public void GenerateRejectsNonCanonicalTopLevelArtifactDirectories(string directoryName)
    {
        var artifacts = Path.Combine(_directory, $"noncanonical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(artifacts, directoryName));
        File.WriteAllText(Path.Combine(artifacts, directoryName, "artifact.zip"), "artifact");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
                Version: "0.1.0",
                ArtifactsDirectory: artifacts,
                ManifestPath: Path.Combine(_directory, "noncanonical-manifest.json"),
                ChecksumsPath: Path.Combine(_directory, "noncanonical-checksums.sha256"),
                NotesPath: null,
                Commit: null,
                GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
                RequiredArtifactKinds: [])));

        Assert.Contains("Unsupported release artifact kind", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateRejectsRootFileNamePrefixKindInference()
    {
        var artifacts = Path.Combine(_directory, "root-prefix");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "api-0.1.0.zip"), "api package");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
                Version: "0.1.0",
                ArtifactsDirectory: artifacts,
                ManifestPath: Path.Combine(_directory, "root-prefix-manifest.json"),
                ChecksumsPath: Path.Combine(_directory, "root-prefix-checksums.sha256"),
                NotesPath: null,
                Commit: null,
                GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
                RequiredArtifactKinds: ["api"])));

        Assert.Contains("canonical top-level", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pluginhost")]
    [InlineData("electron")]
    [InlineData("API")]
    [InlineData(" api")]
    [InlineData("")]
    public void GenerateRejectsNonCanonicalRequiredArtifactKinds(string requiredKind)
    {
        var artifacts = Path.Combine(_directory, $"required-noncanonical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(artifacts, "api"));
        File.WriteAllText(Path.Combine(artifacts, "api", "api.zip"), "api package");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleaseManifestGenerator.Generate(new ReleaseManifestOptions(
                Version: "0.1.0",
                ArtifactsDirectory: artifacts,
                ManifestPath: Path.Combine(_directory, "required-noncanonical-manifest.json"),
                ChecksumsPath: Path.Combine(_directory, "required-noncanonical-checksums.sha256"),
                NotesPath: null,
                Commit: null,
                GeneratedAtUtc: new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
                RequiredArtifactKinds: [requiredKind])));

        Assert.Contains("Unsupported release artifact kind", exception.Message, StringComparison.Ordinal);
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
