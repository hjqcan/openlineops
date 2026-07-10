using System.Text.Json;
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
    public async Task LoadReadsLegacyManifestWithoutPersistingAbsoluteProjectPath()
    {
        var projectRoot = Path.Combine(_testRoot, "legacy");
        Directory.CreateDirectory(projectRoot);
        var legacyPath = Path.Combine(projectRoot, AutomationProjectFileConvention.LegacyProjectFileName);
        await File.WriteAllTextAsync(
            legacyPath,
            """
            {
              "formatVersion": 1,
              "product": "OpenLineOps",
              "projectId": "legacy-line",
              "displayName": "Legacy Line",
              "projectPath": "C:\\stale\\machine\\path",
              "createdAtUtc": "2026-07-10T06:00:00+00:00",
              "updatedAtUtc": "2026-07-10T06:00:00+00:00",
              "activeSnapshotId": null,
              "applications": [
                {
                  "applicationId": "main-line",
                  "displayName": "Main Line",
                  "topologyId": null,
                  "processDefinitionIds": []
                }
              ],
              "snapshots": []
            }
            """);
        var store = new FileSystemAutomationProjectManifestStore();

        var loaded = await store.LoadAsync(projectRoot);

        Assert.NotNull(loaded);
        Assert.Equal(Path.GetFullPath(projectRoot), loaded.ProjectPath);
        Assert.Equal(AutomationProjectManifest.CurrentFormatVersion, loaded.FormatVersion);
        Assert.StartsWith(
            "applications/application-main-line--",
            Assert.Single(loaded.Applications).ProjectFilePath,
            StringComparison.Ordinal);
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
