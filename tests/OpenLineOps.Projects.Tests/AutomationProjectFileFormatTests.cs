using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

namespace OpenLineOps.Projects.Tests;

public sealed class AutomationProjectFileFormatTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-format-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveCreatesRootProjectAndOneApplicationProjectPerFolder()
    {
        var projectRoot = Path.Combine(_testRoot, "PackagingLine");
        var store = new FileSystemAutomationProjectManifestStore();
        var manifest = CreateManifest(projectRoot);

        await store.SaveAsync(manifest);

        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        var applicationFilePath = Path.Combine(
            projectRoot,
            "applications",
            "main-line",
            "main-line.oloapp");
        Assert.True(File.Exists(projectFilePath));
        Assert.True(File.Exists(applicationFilePath));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "topology")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "layouts")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "flows")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "blocks", "custom")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "configuration")));

        using var projectDocument = JsonDocument.Parse(await File.ReadAllTextAsync(projectFilePath));
        Assert.Equal(
            AutomationProjectFile.CurrentSchemaVersion,
            projectDocument.RootElement.GetProperty("schemaVersion").GetString());
        Assert.False(projectDocument.RootElement.TryGetProperty("projectPath", out _));
        Assert.Equal(
            "applications/main-line/main-line.oloapp",
            projectDocument.RootElement
                .GetProperty("applications")[0]
                .GetProperty("projectFile")
                .GetString());

        using var applicationDocument = JsonDocument.Parse(await File.ReadAllTextAsync(applicationFilePath));
        Assert.Equal(
            AutomationApplicationProjectFile.CurrentSchemaVersion,
            applicationDocument.RootElement.GetProperty("schemaVersion").GetString());
        Assert.False(applicationDocument.RootElement.TryGetProperty("projectId", out _));
        Assert.Equal("main-line", applicationDocument.RootElement.GetProperty("applicationId").GetString());
    }

    [Fact]
    public async Task OpenByExplicitProjectFileAfterMovingDirectoryDerivesNewProjectRoot()
    {
        var originalRoot = Path.Combine(_testRoot, "original", "PackagingLine");
        var movedRoot = Path.Combine(_testRoot, "moved", "PackagingLine");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(originalRoot));
        Directory.CreateDirectory(Path.GetDirectoryName(movedRoot)!);
        Directory.Move(originalRoot, movedRoot);

        var loaded = await store.LoadAsync(Path.Combine(movedRoot, "packaging-line.oloproj"));

        Assert.NotNull(loaded);
        Assert.Equal(Path.GetFullPath(movedRoot), loaded.ProjectPath);
        Assert.Equal(
            "applications/main-line/main-line.oloapp",
            Assert.Single(loaded.Applications).ProjectFilePath);
    }

    [Fact]
    public async Task LoadRejectsExistingFileWithoutProjectExtension()
    {
        var projectRoot = Path.Combine(_testRoot, "wrong-extension");
        Directory.CreateDirectory(projectRoot);
        var wrongFilePath = Path.Combine(projectRoot, "openlineops.project.json");
        await File.WriteAllTextAsync(
            wrongFilePath,
            "{}");
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(wrongFilePath));

        Assert.Contains(".oloproj", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadDirectoryWithoutProjectFileReturnsMissing()
    {
        var projectRoot = Path.Combine(_testRoot, "missing");
        Directory.CreateDirectory(projectRoot);
        var store = new FileSystemAutomationProjectManifestStore();

        var loaded = await store.LoadAsync(projectRoot);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadRejectsUnsupportedRootProjectFormat()
    {
        var projectRoot = Path.Combine(_testRoot, "unsupported-root");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(projectRoot));
        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(projectFilePath))!.AsObject();
        document["formatVersion"] = 99;
        await File.WriteAllTextAsync(projectFilePath, document.ToJsonString(JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectFilePath));
    }

    [Fact]
    public async Task LoadRejectsRemovedHostProjectIdFieldInApplicationProject()
    {
        var projectRoot = Path.Combine(_testRoot, "strict-application");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(projectRoot));
        var applicationFilePath = Path.Combine(
            projectRoot,
            "applications",
            "main-line",
            "main-line.oloapp");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(applicationFilePath))!.AsObject();
        document["projectId"] = "removed-host-project";
        await File.WriteAllTextAsync(applicationFilePath, document.ToJsonString(JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectRoot));
    }

    [Fact]
    public async Task LoadRejectsApplicationProjectPathTraversal()
    {
        var projectRoot = Path.Combine(_testRoot, "traversal");
        Directory.CreateDirectory(projectRoot);
        var projectFile = new AutomationProjectFile(
            AutomationProjectFile.CurrentSchemaVersion,
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectFile.KindName,
            AutomationProjectManifest.ProductName,
            "project.traversal",
            "Traversal",
            CreatedAtUtc,
            CreatedAtUtc,
            ActiveSnapshotId: null,
            [new AutomationProjectApplicationReference("application.main", "../escape/main.oloapp")],
            Snapshots: []);
        var projectFilePath = Path.Combine(projectRoot, "project.traversal.oloproj");
        await File.WriteAllTextAsync(
            projectFilePath,
            JsonSerializer.Serialize(projectFile, JsonOptions));
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectFilePath));

        Assert.Contains("../escape/main.oloapp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryWithMultipleProjectFilesIsRejectedAsAmbiguous()
    {
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(Path.Combine(_testRoot, "one.oloproj"), "{}");
        File.WriteAllText(Path.Combine(_testRoot, "two.oloproj"), "{}");
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = Assert.Throws<InvalidDataException>(() => store.GetManifestPath(_testRoot));

        Assert.Contains("multiple", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("releaseManifestPath", true)]
    [InlineData("releaseContentSha256", true)]
    [InlineData("releaseManifestPath", false)]
    [InlineData("releaseContentSha256", false)]
    public async Task LoadRejectsSnapshotWithoutCompleteImmutableReleaseDescriptor(
        string fieldName,
        bool removeField)
    {
        var projectRoot = Path.Combine(_testRoot, $"snapshot-{fieldName}-{removeField}");
        Directory.CreateDirectory(projectRoot);
        var projectFilePath = Path.Combine(projectRoot, "project.oloproj");
        var snapshot = new AutomationProjectSnapshotFile(
            "snapshot.main",
            "application.main",
            "topology.main",
            ["layout.main"],
            "line.main",
            CreatedAtUtc,
            [],
            [],
            [],
            "releases/release-main/release.json",
            new string('a', 64));
        var projectFile = new AutomationProjectFile(
            AutomationProjectFile.CurrentSchemaVersion,
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectFile.KindName,
            AutomationProjectManifest.ProductName,
            "project.main",
            "Project Main",
            CreatedAtUtc,
            CreatedAtUtc,
            null,
            [],
            [snapshot]);
        var document = JsonNode.Parse(JsonSerializer.Serialize(projectFile, JsonOptions))!;
        var snapshotNode = document["snapshots"]![0]!.AsObject();
        if (removeField)
        {
            snapshotNode.Remove(fieldName);
        }
        else
        {
            snapshotNode[fieldName] = string.Empty;
        }

        await File.WriteAllTextAsync(projectFilePath, document.ToJsonString(JsonOptions));
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectFilePath));

        Assert.Contains(fieldName, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static AutomationProjectManifest CreateManifest(string projectRoot)
    {
        return new AutomationProjectManifest(
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectManifest.ProductName,
            "packaging-line",
            "Packaging Line",
            projectRoot,
            CreatedAtUtc,
            CreatedAtUtc,
            ActiveSnapshotId: null,
            [
                new ProjectApplicationManifest(
                    "main-line",
                    "Main Line",
                    TopologyId: null,
                    ProcessDefinitionIds: [],
                    "applications/main-line/main-line.oloapp")
            ],
            Snapshots: []);
    }
}
