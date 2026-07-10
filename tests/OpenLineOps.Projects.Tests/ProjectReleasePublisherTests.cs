using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Releases;

namespace OpenLineOps.Projects.Tests;

public sealed class ProjectReleasePublisherTests
{
    private static readonly DateTimeOffset PublishedAtUtc =
        new(2026, 7, 10, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishAsyncUsesResolvedSourceAndPersistsProjectRelativeReleasePath()
    {
        var calls = new List<string>();
        var projectPath = Path.Combine(Path.GetTempPath(), "openlineops-release-publisher-project");
        var project = CreateProject(projectPath);
        var metadata = CreateMetadata();
        var projectService = new RecordingProjectService(project, calls);
        var workspaceService = new RecordingWorkspaceService(project, calls);
        var scopeResolver = new RecordingScopeResolver(
            new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath),
            calls);
        var sourceResolver = new RecordingSourceResolver(Result.Success(metadata), calls);
        var artifactStore = new RecordingArtifactStore(
            CreateArtifact(projectPath),
            calls);
        var publisher = new ProjectReleasePublisher(
            projectService,
            workspaceService,
            scopeResolver,
            sourceResolver,
            artifactStore,
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "process.main",
                "configuration.main"));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                "project.get",
                "workspace.save",
                "scope.resolve",
                "source.resolve",
                "artifact.publish",
                "source.resolve",
                "project.publish",
                "workspace.save"
            ],
            calls);
        Assert.Same(metadata, artifactStore.PublishedMetadata);
        Assert.Equal(PublishedAtUtc, artifactStore.PublishedAtUtc);
        Assert.NotNull(projectService.PublishRequest);
        Assert.Equal("topology.main", projectService.PublishRequest.TopologyId);
        Assert.Equal(["layout.main", "layout.overview"], projectService.PublishRequest.LayoutIds);
        Assert.Equal("process.main@1.0.0", projectService.PublishRequest.ProcessVersionId);
        Assert.Equal("releases/snapshot.main/release.json", projectService.PublishRequest.ReleaseManifestPath);
        Assert.Equal(new string('a', 64), projectService.PublishRequest.ReleaseContentSha256);
        Assert.Equal("binding.motion", Assert.Single(projectService.PublishRequest.CapabilityBindings).BindingId);
        Assert.Equal("slot.main", Assert.Single(projectService.PublishRequest.TargetReferences).TargetId);
        Assert.Equal(["openlineops_move_axis@1"], projectService.PublishRequest.BlockVersionIds);
        Assert.Equal("topology.main", sourceResolver.TopologyId);
        Assert.Equal("process.main", sourceResolver.ProcessDefinitionId);
        Assert.Equal("configuration.main", sourceResolver.ConfigurationSnapshotId);
    }

    [Fact]
    public async Task PublishAsyncStopsBeforeArtifactWhenSourceResolutionFails()
    {
        var calls = new List<string>();
        var projectPath = Path.Combine(Path.GetTempPath(), "openlineops-release-publisher-source-failure");
        var project = CreateProject(projectPath);
        var expectedError = ApplicationError.Conflict(
            "Projects.ReleaseDriverBindingMissing",
            "Required capability has no binding.");
        var projectService = new RecordingProjectService(project, calls);
        var publisher = new ProjectReleasePublisher(
            projectService,
            new RecordingWorkspaceService(project, calls),
            new RecordingScopeResolver(
                new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath),
                calls),
            new RecordingSourceResolver(
                Result.Failure<ProjectReleaseSourceMetadata>(expectedError),
                calls),
            new RecordingArtifactStore(CreateArtifact(projectPath), calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "process.main",
                "configuration.main"));

        Assert.True(result.IsFailure);
        Assert.Equal(expectedError, result.Error);
        Assert.Equal(["project.get", "workspace.save", "scope.resolve", "source.resolve"], calls);
        Assert.Null(projectService.PublishRequest);
    }

    [Fact]
    public async Task PublishAsyncRejectsArtifactManifestOutsideProjectBeforeSnapshotMutation()
    {
        var calls = new List<string>();
        var projectPath = Path.Combine(Path.GetTempPath(), "openlineops-release-publisher-path-check");
        var project = CreateProject(projectPath);
        var outsidePath = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "outside-release",
            "release.json");
        var artifact = CreateArtifact(projectPath) with { ManifestPath = outsidePath };
        var projectService = new RecordingProjectService(project, calls);
        var publisher = new ProjectReleasePublisher(
            projectService,
            new RecordingWorkspaceService(project, calls),
            new RecordingScopeResolver(
                new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath),
                calls),
            new RecordingSourceResolver(Result.Success(CreateMetadata()), calls),
            new RecordingArtifactStore(artifact, calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "process.main",
                "configuration.main"));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ReleaseArtifactPathOutsideProject", result.Error.Code);
        Assert.Equal(
            ["project.get", "workspace.save", "scope.resolve", "source.resolve", "artifact.publish", "source.resolve"],
            calls);
        Assert.Null(projectService.PublishRequest);
    }

    [Fact]
    public async Task PublishAsyncRejectsCopiedSourceFlowIrMismatchBeforeSnapshotMutation()
    {
        var calls = new List<string>();
        var projectPath = Path.Combine(Path.GetTempPath(), "openlineops-release-publisher-source-race");
        var project = CreateProject(projectPath);
        var sourceResolver = new RecordingSourceResolver(Result.Success(CreateMetadata()), calls)
        {
            CopiedSourceResult = Result.Success(CreateMetadata() with
            {
                FlowIrSha256 = new string('b', 64)
            })
        };
        var projectService = new RecordingProjectService(project, calls);
        var publisher = new ProjectReleasePublisher(
            projectService,
            new RecordingWorkspaceService(project, calls),
            new RecordingScopeResolver(
                new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath),
                calls),
            sourceResolver,
            new RecordingArtifactStore(CreateArtifact(projectPath), calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "process.main",
                "configuration.main"));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ReleaseArtifactSourceMismatch", result.Error.Code);
        Assert.Equal(
            ["project.get", "workspace.save", "scope.resolve", "source.resolve", "artifact.publish", "source.resolve"],
            calls);
        Assert.Null(projectService.PublishRequest);
    }

    [Fact]
    public async Task PublishAsyncRestoresPriorManifestStateWhenManifestSaveFails()
    {
        var calls = new List<string>();
        var projectPath = Path.Combine(Path.GetTempPath(), "openlineops-release-publisher-manifest-failure");
        var previousProject = CreateProject(projectPath);
        var projectService = new RecordingProjectService(previousProject, calls);
        var manifestError = ApplicationError.Validation(
            "Projects.ProjectManifestStorageFailed",
            "Manifest disk is unavailable.");
        var workspaceService = new FailingManifestWorkspaceService(
            previousProject,
            projectService,
            manifestError,
            calls);
        var publisher = new ProjectReleasePublisher(
            projectService,
            workspaceService,
            new RecordingScopeResolver(
                new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath),
                calls),
            new RecordingSourceResolver(Result.Success(CreateMetadata()), calls),
            new RecordingArtifactStore(CreateArtifact(projectPath), calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "process.main",
                "configuration.main"));

        Assert.True(result.IsFailure);
        Assert.Equal(manifestError, result.Error);
        Assert.Equal(
            [
                "project.get",
                "workspace.save",
                "scope.resolve",
                "source.resolve",
                "artifact.publish",
                "source.resolve",
                "project.publish",
                "workspace.save",
                "workspace.open"
            ],
            calls);
        Assert.Null(projectService.CurrentProject.ActiveSnapshotId);
        Assert.Empty(projectService.CurrentProject.Snapshots);
    }

    private static AutomationProjectDetails CreateProject(string projectPath)
    {
        return new AutomationProjectDetails(
            "project.main",
            "Main Project",
            projectPath,
            PublishedAtUtc.AddDays(-1),
            ActiveSnapshotId: null,
            Applications:
            [
                new ProjectApplicationDetails(
                    "application.main",
                    "Main Application",
                    "topology.main",
                    ["process.main"])
            ],
            Snapshots: []);
    }

    private static ProjectReleaseSourceMetadata CreateMetadata()
    {
        return new ProjectReleaseSourceMetadata(
            "topology.main",
            ["layout.main", "layout.overview"],
            "process.main",
            "process.main@1.0.0",
            "openlineops.flow-ir/v1",
            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
            "{}",
            "configuration.main",
            [
                new ProjectReleaseCapabilityBinding(
                    "motion.axis",
                    "binding.motion",
                    "Simulator",
                    "simulator.motion")
            ],
            [new ProjectReleaseTargetReference("Slot", "slot.main")],
            ["openlineops_move_axis@1"]);
    }

    private static ProjectReleaseArtifactDescriptor CreateArtifact(string projectPath)
    {
        var releaseRoot = Path.Combine(projectPath, "releases", "snapshot.main");
        return new ProjectReleaseArtifactDescriptor(
            "snapshot.main",
            "project.main",
            "application.main",
            PublishedAtUtc,
            new string('a', 64),
            releaseRoot,
            Path.Combine(releaseRoot, "source"),
            Path.Combine(releaseRoot, "release.json"),
            Files: []);
    }

    private static AutomationProjectWorkspaceDetails CreateWorkspaceDetails(
        AutomationProjectDetails project)
    {
        return new AutomationProjectWorkspaceDetails(
            project,
            Path.Combine(
                project.ProjectPath,
                AutomationProjectFileConvention.GetProjectFileName(project.ProjectId)),
            new AutomationProjectManifest(
                AutomationProjectManifest.CurrentFormatVersion,
                AutomationProjectManifest.ProductName,
                project.ProjectId,
                project.DisplayName,
                project.ProjectPath,
                project.CreatedAtUtc,
                PublishedAtUtc,
                project.ActiveSnapshotId,
                Applications: [],
                Snapshots: []));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingScopeResolver(
        ProjectApplicationWorkspaceScope scope,
        List<string> calls) : IProjectApplicationWorkspaceScopeResolver
    {
        public ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
            string projectId,
            string applicationId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("scope.resolve");
            return ValueTask.FromResult<ProjectApplicationWorkspaceScope?>(scope);
        }
    }

    private sealed class RecordingSourceResolver(
        Result<ProjectReleaseSourceMetadata> result,
        List<string> calls) : IProjectReleaseSourceResolver
    {
        private int _invocationCount;

        public Result<ProjectReleaseSourceMetadata>? CopiedSourceResult { get; init; }

        public string? TopologyId { get; private set; }

        public string? ProcessDefinitionId { get; private set; }

        public string? ConfigurationSnapshotId { get; private set; }

        public Task<Result<ProjectReleaseSourceMetadata>> ResolveAsync(
            ProjectApplicationWorkspaceScope scope,
            string topologyId,
            string processDefinitionId,
            string configurationSnapshotId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("source.resolve");
            _invocationCount += 1;
            TopologyId = topologyId;
            ProcessDefinitionId = processDefinitionId;
            ConfigurationSnapshotId = configurationSnapshotId;
            return Task.FromResult(_invocationCount > 1 && CopiedSourceResult is not null
                ? CopiedSourceResult
                : result);
        }
    }

    private sealed class RecordingArtifactStore(
        ProjectReleaseArtifactDescriptor descriptor,
        List<string> calls) : IProjectReleaseArtifactStore
    {
        public ProjectReleaseSourceMetadata? PublishedMetadata { get; private set; }

        public DateTimeOffset? PublishedAtUtc { get; private set; }

        public ValueTask<ProjectReleaseArtifactDescriptor> PublishAsync(
            ProjectApplicationWorkspaceScope scope,
            string snapshotId,
            DateTimeOffset publishedAtUtc,
            ProjectReleaseSourceMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            calls.Add("artifact.publish");
            PublishedMetadata = metadata;
            PublishedAtUtc = publishedAtUtc;
            return ValueTask.FromResult(descriptor);
        }

        public ValueTask<OpenedProjectReleaseArtifact?> OpenAsync(
            ProjectApplicationWorkspaceScope scope,
            string snapshotId,
            string expectedContentSha256,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingProjectService(
        AutomationProjectDetails project,
        List<string> calls) : IAutomationProjectService
    {
        public AutomationProjectDetails CurrentProject { get; private set; } = project;

        public PublishProjectSnapshotRequest? PublishRequest { get; private set; }

        public Task<Result<AutomationProjectDetails>> GetByIdAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("project.get");
            return Task.FromResult(Result.Success(CurrentProject));
        }

        public Task<Result<AutomationProjectDetails>> PublishSnapshotAsync(
            string projectId,
            PublishProjectSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("project.publish");
            PublishRequest = request;
            CurrentProject = CurrentProject with { ActiveSnapshotId = request.SnapshotId };
            return Task.FromResult(Result.Success(CurrentProject));
        }

        public void Restore(AutomationProjectDetails restoredProject)
        {
            CurrentProject = restoredProject;
        }

        public Task<Result<AutomationProjectDetails>> CreateAsync(
            CreateAutomationProjectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<IReadOnlyCollection<AutomationProjectSummary>>> ListAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<AutomationProjectDetails>> AddApplicationAsync(
            string projectId,
            AddProjectApplicationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<AutomationProjectDetails>> LinkTopologyAsync(
            string projectId,
            LinkProjectTopologyRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<AutomationProjectDetails>> LinkProcessDefinitionAsync(
            string projectId,
            LinkProjectProcessDefinitionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingWorkspaceService(
        AutomationProjectDetails project,
        List<string> calls) : IAutomationProjectWorkspaceService
    {
        public Task<Result<AutomationProjectWorkspaceDetails>> SaveManifestAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("workspace.save");
            return Task.FromResult(Result.Success(CreateWorkspaceDetails(project)));
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> CreateAsync(
            CreateAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<AutomationProjectWorkspaceDetails>> OpenAsync(
            OpenAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailingManifestWorkspaceService(
        AutomationProjectDetails previousProject,
        RecordingProjectService projectService,
        ApplicationError manifestError,
        List<string> calls) : IAutomationProjectWorkspaceService
    {
        private int _saveCount;

        public Task<Result<AutomationProjectWorkspaceDetails>> SaveManifestAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("workspace.save");
            _saveCount += 1;
            return Task.FromResult(_saveCount == 1
                ? Result.Success(CreateWorkspaceDetails(previousProject))
                : Result.Failure<AutomationProjectWorkspaceDetails>(manifestError));
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> OpenAsync(
            OpenAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("workspace.open");
            projectService.Restore(previousProject);
            return Task.FromResult(Result.Success(CreateWorkspaceDetails(previousProject)));
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> CreateAsync(
            CreateAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
