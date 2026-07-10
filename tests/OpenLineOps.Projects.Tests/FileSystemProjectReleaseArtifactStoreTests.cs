using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.Releases;

namespace OpenLineOps.Projects.Tests;

public sealed class FileSystemProjectReleaseArtifactStoreTests : IDisposable
{
    private static readonly DateTimeOffset PublishedAtUtc = new(2026, 7, 10, 8, 30, 0, TimeSpan.Zero);

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-release-artifact-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishAndOpenRoundTripCopiesApplicationSourceAndNormalizesMetadata()
    {
        var scope = CreateScope("project.release", "application.main");
        WriteSourceFile(scope, "topology/topology.json", "topology-v1");
        WriteSourceFile(scope, "layouts/layout.json", "layout-v1");
        WriteSourceFile(scope, "flows/process-main/flow.json", "flow-v1");
        WriteSourceFile(scope, "flows/process-main/nodes/node-script/generated.py", "print('release')\n");
        Directory.CreateDirectory(Path.Combine(GetApplicationSourcePath(scope), "empty-folder"));
        var store = new FileSystemProjectReleaseArtifactStore();

        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.main.v1",
            PublishedAtUtc,
            CreateUnnormalizedMetadata());
        var opened = await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256);

        Assert.NotNull(opened);
        Assert.Equal(scope.ProjectId, opened.ProjectId);
        Assert.Equal(scope.ApplicationId, opened.ApplicationId);
        Assert.Equal(PublishedAtUtc, opened.PublishedAtUtc);
        Assert.Equal(descriptor.ContentSha256, opened.ContentSha256);
        Assert.Equal(4, opened.Files.Count);
        Assert.All(opened.Files, file =>
        {
            Assert.Equal(64, file.Sha256.Length);
            Assert.Equal(file.Sha256.ToLowerInvariant(), file.Sha256);
        });
        Assert.Equal(["layout.a", "layout.b"], opened.Metadata.LayoutIds);
        Assert.Equal(["block.inspect@1", "block.move@2"], opened.Metadata.BlockVersionIds);
        Assert.Equal(["capability.a", "capability.b"], opened.Metadata.CapabilityBindings
            .Select(binding => binding.CapabilityId));
        Assert.Equal(["Slot", "Station"], opened.Metadata.TargetReferences.Select(target => target.Kind));
        Assert.Equal("openlineops.flow-ir/v1", opened.Metadata.FlowIrSchemaVersion);
        Assert.Equal("{}", opened.Metadata.FlowIrCanonicalJson);
        Assert.Equal(
            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
            opened.Metadata.FlowIrSha256);

        var releaseSourceScope = new ProjectApplicationWorkspaceScope(
            opened.ProjectId,
            opened.ApplicationId,
            opened.SourceRootPath,
            opened.ApplicationProjectRelativePath);
        var releaseApplicationPath = GetApplicationSourcePath(releaseSourceScope);
        Assert.Equal("flow-v1", File.ReadAllText(Path.Combine(releaseApplicationPath, "flows", "process-main", "flow.json")));
        Assert.True(Directory.Exists(Path.Combine(releaseApplicationPath, "empty-folder")));
        Assert.StartsWith(Path.GetFullPath(scope.ProjectPath), descriptor.ReleaseRootPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(descriptor.ReleaseRootPath, "source"), descriptor.SourceRootPath);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath));
        Assert.Equal(
            "openlineops.project-release-artifact",
            manifest.RootElement.GetProperty("schema").GetString());
        Assert.Equal(3, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.DoesNotContain(Path.GetFullPath(scope.ProjectPath), await File.ReadAllTextAsync(descriptor.ManifestPath));
    }

    [Fact]
    public async Task PublishPreservesExplicitApplicationProjectLayoutInsideRelease()
    {
        var projectPath = Path.Combine(_testRoot, "explicit-project");
        var scope = new ProjectApplicationWorkspaceScope(
            "project.explicit",
            "application.main",
            projectPath,
            "applications/Main Line/MainLine.oloapp");
        WriteSourceFile(scope, "MainLine.oloapp", "{\"applicationId\":\"application.main\"}");
        WriteSourceFile(scope, "flows/process-main/flow.json", "explicit-flow");
        var store = new FileSystemProjectReleaseArtifactStore();

        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.explicit",
            PublishedAtUtc,
            CreateMetadata());
        var opened = Assert.IsType<OpenedProjectReleaseArtifact>(
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));
        var releaseScope = new ProjectApplicationWorkspaceScope(
            opened.ProjectId,
            opened.ApplicationId,
            opened.SourceRootPath,
            opened.ApplicationProjectRelativePath);

        Assert.Equal(
            "explicit-flow",
            File.ReadAllText(Path.Combine(
                releaseScope.ApplicationRootPath,
                "flows",
                "process-main",
                "flow.json")));
        Assert.True(File.Exists(Path.Combine(releaseScope.ApplicationRootPath, "MainLine.oloapp")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath));
        var internalApplicationPath = manifest.RootElement
            .GetProperty("sourceApplicationRelativePath")
            .GetString();
        Assert.Equal("applications/Main Line", internalApplicationPath);
        Assert.Equal(
            "applications/Main Line/MainLine.oloapp",
            manifest.RootElement.GetProperty("applicationProjectRelativePath").GetString());
    }

    [Fact]
    public async Task PublishRefusesOverwriteAndPreservesOriginalRelease()
    {
        var scope = CreateScope("project.immutable", "application.main");
        var sourcePath = WriteSourceFile(scope, "flows/process-main/flow.json", "original-flow");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.immutable",
            PublishedAtUtc,
            CreateMetadata());
        await File.WriteAllTextAsync(sourcePath, "changed-draft-flow");

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.immutable",
                PublishedAtUtc.AddMinutes(1),
                CreateMetadata()));
        var opened = await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256);
        var releaseScope = new ProjectApplicationWorkspaceScope(
            opened!.ProjectId,
            opened.ApplicationId,
            opened.SourceRootPath,
            opened.ApplicationProjectRelativePath);

        Assert.Contains("immutable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "original-flow",
            File.ReadAllText(Path.Combine(GetApplicationSourcePath(releaseScope), "flows", "process-main", "flow.json")));
    }

    [Fact]
    public async Task OpenRejectsTamperedSourceFile()
    {
        var scope = CreateScope("project.tamper", "application.main");
        WriteSourceFile(scope, "configuration/project.json", "configuration-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.tamper",
            PublishedAtUtc,
            CreateMetadata());
        var releaseScope = new ProjectApplicationWorkspaceScope(
            scope.ProjectId,
            scope.ApplicationId,
            descriptor.SourceRootPath,
            descriptor.ApplicationProjectRelativePath);
        await File.WriteAllTextAsync(
            Path.Combine(GetApplicationSourcePath(releaseScope), "configuration", "project.json"),
            "configuration-v2");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("SHA-256", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishRejectsFlowIrHashMismatch()
    {
        var scope = CreateScope("project.flow-ir-hash", "application.main");
        WriteSourceFile(scope, "flows/process-main/flow.json", "flow-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var metadata = CreateMetadata() with { FlowIrSha256 = new string('a', 64) };

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.flow-ir-hash",
                PublishedAtUtc,
                metadata));

        Assert.Contains("FlowIrSha256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsInvalidFlowIrJsonEvenWhenHashMatches()
    {
        const string invalidJson = "not-json";
        var scope = CreateScope("project.flow-ir-json", "application.main");
        WriteSourceFile(scope, "flows/process-main/flow.json", "flow-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var metadata = CreateMetadata() with
        {
            FlowIrCanonicalJson = invalidJson,
            FlowIrSha256 = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(invalidJson)))
                .ToLowerInvariant()
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PublishAsync(
                scope,
                "snapshot.flow-ir-json",
                PublishedAtUtc,
                metadata));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRejectsExtraFileAnywhereInRelease()
    {
        var scope = CreateScope("project.extra", "application.main");
        WriteSourceFile(scope, "topology/topology.json", "topology-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.extra",
            PublishedAtUtc,
            CreateMetadata());
        await File.WriteAllTextAsync(Path.Combine(descriptor.ReleaseRootPath, "untracked.txt"), "untracked");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("file set differs", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untracked.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRejectsMissingSourceFile()
    {
        var scope = CreateScope("project.missing", "application.main");
        WriteSourceFile(scope, "topology/topology.json", "topology-v1");
        WriteSourceFile(scope, "layouts/layout.json", "layout-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.missing",
            PublishedAtUtc,
            CreateMetadata());
        var releaseScope = new ProjectApplicationWorkspaceScope(
            scope.ProjectId,
            scope.ApplicationId,
            descriptor.SourceRootPath,
            descriptor.ApplicationProjectRelativePath);
        File.Delete(Path.Combine(GetApplicationSourcePath(releaseScope), "layouts", "layout.json"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("file set differs", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("layout.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRejectsManifestFilePathTraversal()
    {
        var scope = CreateScope("project.path", "application.main");
        WriteSourceFile(scope, "topology/topology.json", "topology-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.path",
            PublishedAtUtc,
            CreateMetadata());
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath))!.AsObject();
        manifest["files"]!.AsArray()[0]!["relativePath"] = "../escaped.json";
        await File.WriteAllTextAsync(descriptor.ManifestPath, manifest.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("outside the application source", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRejectsPriorReleaseSchemaVersion()
    {
        var scope = CreateScope("project.old-release", "application.main");
        WriteSourceFile(scope, "topology/topology.json", "topology-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.old-release",
            PublishedAtUtc,
            CreateMetadata());
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath))!.AsObject();
        manifest["schemaVersion"] = 2;
        await File.WriteAllTextAsync(descriptor.ManifestPath, manifest.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("schema version 2", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRejectsUnknownReleaseManifestField()
    {
        var scope = CreateScope("project.unknown-field", "application.main");
        WriteSourceFile(scope, "topology/topology.json", "topology-v1");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            scope,
            "snapshot.unknown-field",
            PublishedAtUtc,
            CreateMetadata());
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(descriptor.ManifestPath))!.AsObject();
        manifest["unexpectedSourcePath"] = "applications/application.main";
        await File.WriteAllTextAsync(descriptor.ManifestPath, manifest.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scope, descriptor.SnapshotId, descriptor.ContentSha256));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseOpensAfterWholeProjectFolderMoves()
    {
        var originalProjectPath = Path.Combine(_testRoot, "original-project");
        var movedProjectPath = Path.Combine(_testRoot, "moved-project");
        var originalScope = new ProjectApplicationWorkspaceScope(
            "project.movable",
            "application.main",
            originalProjectPath,
            ApplicationProjectPath("application.main"));
        WriteSourceFile(originalScope, "flows/process-main/flow.json", "portable-flow");
        var store = new FileSystemProjectReleaseArtifactStore();
        var descriptor = await store.PublishAsync(
            originalScope,
            "snapshot.portable",
            PublishedAtUtc,
            CreateMetadata());

        Directory.Move(originalProjectPath, movedProjectPath);
        var movedScope = new ProjectApplicationWorkspaceScope(
            originalScope.ProjectId,
            originalScope.ApplicationId,
            movedProjectPath,
            originalScope.ApplicationProjectRelativePath);
        var opened = await store.OpenAsync(movedScope, descriptor.SnapshotId, descriptor.ContentSha256);

        Assert.NotNull(opened);
        Assert.StartsWith(Path.GetFullPath(movedProjectPath), opened.ReleaseRootPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(originalProjectPath), opened.ManifestPath, StringComparison.OrdinalIgnoreCase);
        var releaseScope = new ProjectApplicationWorkspaceScope(
            opened.ProjectId,
            opened.ApplicationId,
            opened.SourceRootPath,
            opened.ApplicationProjectRelativePath);
        Assert.Equal(
            "portable-flow",
            File.ReadAllText(Path.Combine(GetApplicationSourcePath(releaseScope), "flows", "process-main", "flow.json")));
    }

    [Fact]
    public async Task ApplicationAAndBAreIsolatedAndWrongScopeIsRejected()
    {
        var projectPath = Path.Combine(_testRoot, "project-ab");
        var scopeA = new ProjectApplicationWorkspaceScope(
            "project.ab",
            "application.a",
            projectPath,
            ApplicationProjectPath("application.a"));
        var scopeB = new ProjectApplicationWorkspaceScope(
            "project.ab",
            "application.b",
            projectPath,
            ApplicationProjectPath("application.b"));
        WriteSourceFile(scopeA, "flows/shared/flow.json", "flow-from-a");
        WriteSourceFile(scopeB, "flows/shared/flow.json", "flow-from-b");
        var store = new FileSystemProjectReleaseArtifactStore();

        var releaseA = await store.PublishAsync(
            scopeA,
            "snapshot.a",
            PublishedAtUtc,
            CreateMetadata("topology.a"));
        var releaseB = await store.PublishAsync(
            scopeB,
            "snapshot.b",
            PublishedAtUtc.AddMinutes(1),
            CreateMetadata("topology.b"));
        var openedA = await store.OpenAsync(scopeA, releaseA.SnapshotId, releaseA.ContentSha256);
        var openedB = await store.OpenAsync(scopeB, releaseB.SnapshotId, releaseB.ContentSha256);

        Assert.NotNull(openedA);
        Assert.NotNull(openedB);
        Assert.NotEqual(openedA.ReleaseRootPath, openedB.ReleaseRootPath);
        Assert.Equal("topology.a", openedA.Metadata.TopologyId);
        Assert.Equal("topology.b", openedB.Metadata.TopologyId);
        Assert.Equal("flow-from-a", ReadReleasedFlow(openedA));
        Assert.Equal("flow-from-b", ReadReleasedFlow(openedB));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.OpenAsync(scopeB, releaseA.SnapshotId, releaseA.ContentSha256));
        Assert.Contains("scope is", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private ProjectApplicationWorkspaceScope CreateScope(string projectId, string applicationId)
    {
        return new ProjectApplicationWorkspaceScope(
            projectId,
            applicationId,
            Path.Combine(_testRoot, "project"),
            ApplicationProjectPath(applicationId));
    }

    private static ProjectReleaseSourceMetadata CreateMetadata(string topologyId = "topology.main")
    {
        return new ProjectReleaseSourceMetadata(
            topologyId,
            ["layout.main"],
            "process.main",
            "process.main@1.0.0",
            "openlineops.flow-ir/v1",
            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
            "{}",
            "configuration.main.v1",
            [new ProjectReleaseCapabilityBinding("capability.main", "binding.main", "Simulator", "simulator.main")],
            [new ProjectReleaseTargetReference("Station", "station.main")],
            ["block.main@1"]);
    }

    private static ProjectReleaseSourceMetadata CreateUnnormalizedMetadata()
    {
        return new ProjectReleaseSourceMetadata(
            " topology.main ",
            ["layout.b", " layout.a ", "layout.b"],
            " process.main ",
            " process.main@1.0.0 ",
            " openlineops.flow-ir/v1 ",
            " 44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a ",
            "{}",
            " configuration.main.v1 ",
            [
                new ProjectReleaseCapabilityBinding("capability.b", "binding.b", " Simulator ", "simulator.b"),
                new ProjectReleaseCapabilityBinding(" capability.a ", "binding.a", "Simulator", "simulator.a"),
                new ProjectReleaseCapabilityBinding("capability.a", "binding.a", "Simulator", "simulator.a")
            ],
            [
                new ProjectReleaseTargetReference("Station", "station.main"),
                new ProjectReleaseTargetReference(" Slot ", "slot.main")
            ],
            ["block.move@2", " block.inspect@1 ", "block.move@2"]);
    }

    private static string WriteSourceFile(
        ProjectApplicationWorkspaceScope scope,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            GetApplicationSourcePath(scope),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ReadReleasedFlow(OpenedProjectReleaseArtifact release)
    {
        var scope = new ProjectApplicationWorkspaceScope(
            release.ProjectId,
            release.ApplicationId,
            release.SourceRootPath,
            release.ApplicationProjectRelativePath);
        return File.ReadAllText(Path.Combine(GetApplicationSourcePath(scope), "flows", "shared", "flow.json"));
    }

    private static string ApplicationProjectPath(string applicationId)
    {
        return $"applications/{applicationId}/{applicationId}.oloapp";
    }

    private static string GetApplicationSourcePath(ProjectApplicationWorkspaceScope scope)
    {
        return scope.ApplicationRootPath;
    }
}
