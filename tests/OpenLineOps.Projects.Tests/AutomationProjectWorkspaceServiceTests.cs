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
                "process.main",
                "process.main@1.0.0",
                "configuration.main.v1",
                [
                    new SnapshotCapabilityBindingRequest(
                        "motion.axis.move",
                        "binding.axis.x",
                        "Simulator",
                        "simulator.axis.x")
                ],
                [new ProjectTargetReferenceRequest("slot", "slot.left.1")],
                ["block.motion.axis.move@1.0.0"]));
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
        Assert.True(opened.IsSuccess);
        Assert.Equal(Path.GetFullPath(_projectDirectory), opened.Value.Project.ProjectPath);
        Assert.Equal("snapshot.main.v1", opened.Value.Project.ActiveSnapshotId);
        Assert.Single(opened.Value.Project.Applications);
        Assert.Single(opened.Value.Project.Snapshots);
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
}
