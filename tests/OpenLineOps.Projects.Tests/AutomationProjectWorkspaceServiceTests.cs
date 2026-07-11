using System.Text.Json;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Infrastructure.Persistence;
using OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

namespace OpenLineOps.Projects.Tests;

public sealed class AutomationProjectWorkspaceServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 3, 0, 0, TimeSpan.Zero);

    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-workspace-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateSaveAndOpenWorkspaceRoundTripsProjectManifest()
    {
        var repository = new InMemoryAutomationProjectRepository();
        var manifestStore = new FileSystemAutomationProjectManifestStore();
        var workspaceService = new AutomationProjectWorkspaceService(
            repository,
            manifestStore,
            new FixedClock(Now));
        var projectService = new AutomationProjectService(repository, new FixedClock(Now));

        var created = await workspaceService.CreateAsync(new CreateAutomationProjectWorkspaceRequest(
            "project.workspace",
            "Workspace Project",
            _projectDirectory,
            "application.main",
            "Main Application"));
        var topologyLinked = await projectService.LinkTopologyAsync(
            "project.workspace",
            new LinkProjectTopologyRequest("application.main", "topology.main"));
        var processLinked = await projectService.LinkProcessDefinitionAsync(
            "project.workspace",
            new LinkProjectProcessDefinitionRequest("application.main", "process.main"));
        var published = await projectService.PublishSnapshotAsync(
            "project.workspace",
            new PublishProjectSnapshotRequest(
                "snapshot.main.v1",
                "application.main",
                "topology.main",
                ["layout.main"],
                "line.main",
                [
                    new SnapshotCapabilityBindingRequest(
                        "motion.axis.move",
                        "binding.axis.x",
                        "Simulator",
                        "simulator.axis.x",
                        "station.main",
                        "station.main")
                ],
                [new ProjectTargetReferenceRequest("slot", "slot.left.1")],
                ["block.motion.axis.move@1.0.0"],
                "releases/release-main/release.json",
                new string('a', 64)));
        var saved = await workspaceService.SaveManifestAsync("project.workspace");

        var openedRepository = new InMemoryAutomationProjectRepository();
        var openedService = new AutomationProjectWorkspaceService(
            openedRepository,
            manifestStore,
            new FixedClock(Now.AddMinutes(1)));
        var opened = await openedService.OpenAsync(new OpenAutomationProjectWorkspaceRequest(_projectDirectory));

        Assert.True(created.IsSuccess);
        Assert.True(topologyLinked.IsSuccess);
        Assert.True(processLinked.IsSuccess);
        Assert.True(published.IsSuccess);
        Assert.True(saved.IsSuccess);
        Assert.True(File.Exists(saved.Value.ManifestPath));
        using (var projectFile = JsonDocument.Parse(await File.ReadAllTextAsync(saved.Value.ManifestPath)))
        {
            var frozenSnapshot = Assert.Single(
                projectFile.RootElement.GetProperty("snapshots").EnumerateArray());
            Assert.Equal("line.main", frozenSnapshot.GetProperty("productionLineDefinitionId").GetString());
            Assert.False(frozenSnapshot.TryGetProperty("processDefinitionId", out _));
            Assert.False(frozenSnapshot.TryGetProperty("processVersionId", out _));
            Assert.False(frozenSnapshot.TryGetProperty("configurationSnapshotId", out _));
        }
        Assert.True(opened.IsSuccess);
        Assert.Equal(Path.GetFullPath(_projectDirectory), opened.Value.Project.ProjectPath);
        Assert.Equal("snapshot.main.v1", opened.Value.Project.ActiveSnapshotId);
        Assert.Single(opened.Value.Project.Applications);
        Assert.Single(opened.Value.Project.Snapshots);
        Assert.Equal("layout.main", Assert.Single(opened.Value.Project.Snapshots.Single().LayoutIds));
        Assert.Equal(new string('a', 64), opened.Value.Project.Snapshots.Single().ReleaseContentSha256);
        Assert.Equal("topology.main", opened.Value.Project.Applications.Single().TopologyId);
        Assert.Contains("process.main", opened.Value.Project.Applications.Single().ProcessDefinitionIds);
        Assert.Equal("snapshot.main.v1", opened.Value.Manifest.ActiveSnapshotId);
    }

    [Fact]
    public async Task CreateWorkspaceRejectsExistingProjectManifest()
    {
        var repository = new InMemoryAutomationProjectRepository();
        var service = new AutomationProjectWorkspaceService(
            repository,
            new FileSystemAutomationProjectManifestStore(),
            new FixedClock(Now));
        var request = new CreateAutomationProjectWorkspaceRequest(
            "project.workspace",
            "Workspace Project",
            _projectDirectory,
            null,
            null);

        var first = await service.CreateAsync(request);
        var duplicate = await service.CreateAsync(request with { ProjectId = "project.workspace.duplicate" });

        Assert.True(first.IsSuccess);
        Assert.True(duplicate.IsFailure);
        Assert.Equal("Conflict.Projects.ManifestAlreadyExists", duplicate.Error.Code);
    }

    [Fact]
    public async Task CreateWorkspaceFileFailureDoesNotRegisterProject()
    {
        var repository = new InMemoryAutomationProjectRepository();
        var service = new AutomationProjectWorkspaceService(
            repository,
            new FailingSaveManifestStore(new FileSystemAutomationProjectManifestStore()),
            new FixedClock(Now));

        var result = await service.CreateAsync(new CreateAutomationProjectWorkspaceRequest(
            "project.failed",
            "Failed Project",
            _projectDirectory,
            "application.main",
            "Main Application"));

        Assert.True(result.IsFailure);
        Assert.Null(await repository.GetByIdAsync(
            new OpenLineOps.Projects.Domain.Identifiers.AutomationProjectId("project.failed")));
    }

    [Fact]
    public async Task CopiedApplicationProjectCanBeImportedIntoAnotherAutomationProject()
    {
        var repository = new InMemoryAutomationProjectRepository();
        var manifestStore = new FileSystemAutomationProjectManifestStore();
        var workspaceService = new AutomationProjectWorkspaceService(
            repository,
            manifestStore,
            new FixedClock(Now));
        var projectService = new AutomationProjectService(repository, new FixedClock(Now));
        var projectARoot = Path.Combine(_projectDirectory, "project-a");
        var projectBRoot = Path.Combine(_projectDirectory, "project-b");

        var projectA = await workspaceService.CreateAsync(new CreateAutomationProjectWorkspaceRequest(
            "project.a",
            "Project A",
            projectARoot,
            "app1",
            "Reusable App"));
        Assert.True(projectA.IsSuccess, projectA.Error.Message);
        Assert.True((await projectService.LinkTopologyAsync(
            "project.a",
            new LinkProjectTopologyRequest("app1", "topology.app1"))).IsSuccess);
        Assert.True((await projectService.LinkProcessDefinitionAsync(
            "project.a",
            new LinkProjectProcessDefinitionRequest("app1", "process.app1"))).IsSuccess);
        Assert.True((await workspaceService.SaveManifestAsync("project.a")).IsSuccess);

        var projectB = await workspaceService.CreateAsync(new CreateAutomationProjectWorkspaceRequest(
            "project.b",
            "Project B",
            projectBRoot,
            null,
            null));
        Assert.True(projectB.IsSuccess, projectB.Error.Message);

        var sourceApplicationRoot = Path.Combine(projectARoot, "applications", "app1");
        var copiedApplicationRoot = Path.Combine(projectBRoot, "applications", "copied-app1");
        CopyDirectory(sourceApplicationRoot, copiedApplicationRoot);
        var copiedApplicationFile = Path.Combine(copiedApplicationRoot, "app1.oloapp");
        var copiedBytes = await File.ReadAllBytesAsync(copiedApplicationFile);
        var copiedTimestamp = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(copiedApplicationFile, copiedTimestamp);

        var imported = await workspaceService.ImportApplicationAsync(
            "project.b",
            new ImportAutomationProjectApplicationRequest(copiedApplicationFile));

        Assert.True(imported.IsSuccess, imported.Error.Message);
        var application = Assert.Single(imported.Value.Project.Applications);
        Assert.Equal("app1", application.ApplicationId);
        Assert.Equal("topology.app1", application.TopologyId);
        Assert.Contains("process.app1", application.ProcessDefinitionIds);
        Assert.Equal(
            "applications/copied-app1/app1.oloapp",
            application.ProjectFilePath);
        Assert.Equal(
            "applications/copied-app1/app1.oloapp",
            Assert.Single(imported.Value.Manifest.Applications).ProjectFilePath);
        Assert.Equal(copiedBytes, await File.ReadAllBytesAsync(copiedApplicationFile));
        Assert.Equal(copiedTimestamp, File.GetLastWriteTimeUtc(copiedApplicationFile));
    }

    [Fact]
    public async Task ImportFileFailureDoesNotMutateRegisteredProject()
    {
        var repository = new InMemoryAutomationProjectRepository();
        var store = new FileSystemAutomationProjectManifestStore();
        var service = new AutomationProjectWorkspaceService(repository, store, new FixedClock(Now));
        var sourceRoot = Path.Combine(_projectDirectory, "source");
        var targetRoot = Path.Combine(_projectDirectory, "target");
        Assert.True((await service.CreateAsync(new CreateAutomationProjectWorkspaceRequest(
            "project.source",
            "Source",
            sourceRoot,
            "application.portable",
            "Portable"))).IsSuccess);
        Assert.True((await service.CreateAsync(new CreateAutomationProjectWorkspaceRequest(
            "project.target",
            "Target",
            targetRoot,
            null,
            null))).IsSuccess);

        var copiedRoot = Path.Combine(targetRoot, "applications", "portable");
        CopyDirectory(Path.Combine(sourceRoot, "applications", "application.portable"), copiedRoot);
        var failingService = new AutomationProjectWorkspaceService(
            repository,
            new FailingSaveManifestStore(store),
            new FixedClock(Now));

        var result = await failingService.ImportApplicationAsync(
            "project.target",
            new ImportAutomationProjectApplicationRequest(Path.Combine(
                copiedRoot,
                "application.portable.oloapp")));

        Assert.True(result.IsFailure);
        var registered = await repository.GetByIdAsync(
            new OpenLineOps.Projects.Domain.Identifiers.AutomationProjectId("project.target"));
        Assert.NotNull(registered);
        Assert.Empty(registered.Applications);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDirectory))
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class FailingSaveManifestStore(IAutomationProjectManifestStore inner)
        : IAutomationProjectManifestStore
    {
        public string GetProjectRootPath(string projectTarget) => inner.GetProjectRootPath(projectTarget);

        public string GetManifestPath(string projectTarget, string? projectId = null) =>
            inner.GetManifestPath(projectTarget, projectId);

        public ValueTask SaveAsync(
            AutomationProjectManifest manifest,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Manifest write failed."));

        public ValueTask<AutomationProjectManifest?> LoadAsync(
            string projectTarget,
            CancellationToken cancellationToken = default) =>
            inner.LoadAsync(projectTarget, cancellationToken);

        public ValueTask<ProjectApplicationManifest?> LoadApplicationProjectAsync(
            string projectRootPath,
            string applicationProjectTarget,
            CancellationToken cancellationToken = default) =>
            inner.LoadApplicationProjectAsync(
                projectRootPath,
                applicationProjectTarget,
                cancellationToken);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
