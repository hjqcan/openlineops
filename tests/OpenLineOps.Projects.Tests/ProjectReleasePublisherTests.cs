using System.Security.Cryptography;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.Releases;

namespace OpenLineOps.Projects.Tests;

public sealed class ProjectReleasePublisherTests
{
    private const string ApplicationProjectPath = "applications/application.main/application.main.oloapp";
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
            new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath, ApplicationProjectPath),
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
            new RecordingStationPackagePublisher(calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "line.main"));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                "project.get",
                "workspace.save",
                "scope.resolve",
                "source.resolve",
                "station-packages.validate",
                "artifact.publish",
                "source.resolve",
                "station-packages.publish",
                "project.publish",
                "workspace.save"
            ],
            calls);
        Assert.Same(metadata, artifactStore.PublishedMetadata);
        Assert.Equal(PublishedAtUtc, artifactStore.PublishedAtUtc);
        Assert.NotNull(projectService.PublishRequest);
        Assert.Equal("topology.main", projectService.PublishRequest.TopologyId);
        Assert.Equal("line.main", projectService.PublishRequest.ProductionLineDefinitionId);
        Assert.Equal(["layout.main", "layout.overview"], projectService.PublishRequest.LayoutIds);
        Assert.Equal("releases/snapshot.main/release.json", projectService.PublishRequest.ReleaseManifestPath);
        Assert.Equal(new string('a', 64), projectService.PublishRequest.ReleaseContentSha256);
        Assert.Equal("binding.motion", Assert.Single(projectService.PublishRequest.CapabilityBindings).BindingId);
        Assert.Equal("slot.main", Assert.Single(projectService.PublishRequest.TargetReferences).TargetId);
        Assert.Equal(["openlineops_move_axis@1"], projectService.PublishRequest.BlockVersionIds);
        Assert.Equal("topology.main", sourceResolver.TopologyId);
        Assert.Equal("line.main", sourceResolver.ProductionLineDefinitionId);
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
                new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath, ApplicationProjectPath),
                calls),
            new RecordingSourceResolver(
                Result.Failure<ProjectReleaseSourceMetadata>(expectedError),
                calls),
            new RecordingArtifactStore(CreateArtifact(projectPath), calls),
            new RecordingStationPackagePublisher(calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "line.main"));

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
                new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath, ApplicationProjectPath),
                calls),
            new RecordingSourceResolver(Result.Success(CreateMetadata()), calls),
            new RecordingArtifactStore(artifact, calls),
            new RecordingStationPackagePublisher(calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "line.main"));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ReleaseArtifactPathOutsideProject", result.Error.Code);
        Assert.Equal(
            ["project.get", "workspace.save", "scope.resolve", "source.resolve", "station-packages.validate", "artifact.publish", "source.resolve", "artifact.rollback"],
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
            CopiedSourceResult = Result.Success(CreateMetadataWithChangedFlowIr())
        };
        var projectService = new RecordingProjectService(project, calls);
        var publisher = new ProjectReleasePublisher(
            projectService,
            new RecordingWorkspaceService(project, calls),
            new RecordingScopeResolver(
                new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath, ApplicationProjectPath),
                calls),
            sourceResolver,
            new RecordingArtifactStore(CreateArtifact(projectPath), calls),
            new RecordingStationPackagePublisher(calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "line.main"));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ReleaseArtifactSourceMismatch", result.Error.Code);
        Assert.Equal(
            ["project.get", "workspace.save", "scope.resolve", "source.resolve", "station-packages.validate", "artifact.publish", "source.resolve", "artifact.rollback"],
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
                new ProjectApplicationWorkspaceScope("project.main", "application.main", projectPath, ApplicationProjectPath),
                calls),
            new RecordingSourceResolver(Result.Success(CreateMetadata()), calls),
            new RecordingArtifactStore(CreateArtifact(projectPath), calls),
            new RecordingStationPackagePublisher(calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest(
                "snapshot.main",
                "application.main",
                "line.main"));

        Assert.True(result.IsFailure);
        Assert.Equal(manifestError, result.Error);
        Assert.Equal(
            [
                "project.get",
                "workspace.save",
                "scope.resolve",
                "source.resolve",
                "station-packages.validate",
                "artifact.publish",
                "source.resolve",
                "station-packages.publish",
                "project.publish",
                "workspace.save",
                "workspace.open"
                ,"station-packages.rollback"
                ,"artifact.rollback"
            ],
            calls);
        Assert.Null(projectService.CurrentProject.ActiveSnapshotId);
        Assert.Empty(projectService.CurrentProject.Snapshots);
    }

    [Fact]
    public async Task InvalidSigningConfigurationLeavesNoReleaseAndSameSnapshotSucceedsAfterRepair()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-release-preflight-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(root, "project");
        var applicationRoot = Path.Combine(projectPath, "applications", "application.main");
        var distribution = Path.Combine(root, "distribution");
        var catalog = Path.Combine(root, "catalog");
        var privateKeyPath = Path.Combine(root, "keys", "release-private.pem");
        Directory.CreateDirectory(applicationRoot);
        File.WriteAllText(
            Path.Combine(applicationRoot, "application.main.oloapp"),
            "{\"applicationId\":\"application.main\"}");
        WriteFrozenApplicationResources(applicationRoot);
        var calls = new List<string>();
        var project = CreateProject(projectPath);
        var metadata = CreateMetadata();
        metadata = metadata with
        {
            TargetReferences =
            [
                .. metadata.TargetReferences,
                new ProjectReleaseTargetReference("System", "station.main"),
                new ProjectReleaseTargetReference("System", "station.eol")
            ]
        };
        var stationPackages = new FileSystemProjectReleaseStationPackagePublisher(
            new StationPackagePublicationOptions(
                distribution,
                catalog,
                "test-release-signing",
                privateKeyPath));
        var publisher = new ProjectReleasePublisher(
            new RecordingProjectService(project, calls),
            new RecordingWorkspaceService(project, calls),
            new RecordingScopeResolver(
                new ProjectApplicationWorkspaceScope(
                    "project.main",
                    "application.main",
                    projectPath,
                    ApplicationProjectPath),
                calls),
            new RecordingSourceResolver(Result.Success(metadata), calls),
            new FileSystemProjectReleaseArtifactStore(),
            stationPackages,
            new FixedClock(PublishedAtUtc));
        var request = new PublishProjectReleaseRequest(
            "snapshot.main",
            "application.main",
            "line.main");
        try
        {
            var failed = await publisher.PublishAsync("project.main", request);

            Assert.True(failed.IsFailure);
            Assert.Equal(
                "Conflict.Projects.StationPackageConfigurationInvalid",
                failed.Error.Code);
            Assert.False(Directory.Exists(Path.Combine(projectPath, "releases")));
            Assert.False(Directory.Exists(distribution));
            Assert.False(Directory.Exists(catalog));

            Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
            using (var rsa = RSA.Create(3072))
            {
                await File.WriteAllTextAsync(privateKeyPath, rsa.ExportRSAPrivateKeyPem());
            }

            var repaired = await publisher.PublishAsync("project.main", request);

            Assert.True(repaired.IsSuccess, repaired.IsFailure ? repaired.Error.Message : string.Empty);
            Assert.True(Directory.Exists(Path.Combine(projectPath, "releases")));
            Assert.Single(Directory.EnumerateFiles(distribution, "*.olopkg"));
            Assert.Single(Directory.EnumerateFiles(catalog, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SigningPrivateKeyInsideAutomationProjectIsRejectedBeforePublication()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-release-key-boundary-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(root, "project");
        var applicationRoot = Path.Combine(projectPath, "applications", "application.main");
        var keyPath = Path.Combine(projectPath, "secrets", "release-private.pem");
        Directory.CreateDirectory(applicationRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        using (var rsa = RSA.Create(3072))
        {
            await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem());
        }

        var distribution = Path.Combine(root, "distribution");
        var catalog = Path.Combine(root, "catalog");
        var publisher = new FileSystemProjectReleaseStationPackagePublisher(
            new StationPackagePublicationOptions(
                distribution,
                catalog,
                "test-release-signing",
                keyPath));
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await publisher.ValidateConfigurationAsync(new(
                    new ProjectApplicationWorkspaceScope(
                        "project.main",
                        "application.main",
                        projectPath,
                        ApplicationProjectPath),
                    "snapshot.main")));

            Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(projectPath, "releases")));
            Assert.False(Directory.Exists(distribution));
            Assert.False(Directory.Exists(catalog));
            Assert.Empty(Directory.EnumerateFiles(root, "*.olopkg", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StationPackageFailureRollsBackFrozenReleaseBeforeSnapshotMutation()
    {
        var calls = new List<string>();
        var projectPath = Path.Combine(Path.GetTempPath(), $"openlineops-package-rollback-{Guid.NewGuid():N}");
        var project = CreateProject(projectPath);
        var projectService = new RecordingProjectService(project, calls);
        var publisher = new ProjectReleasePublisher(
            projectService,
            new RecordingWorkspaceService(project, calls),
            new RecordingScopeResolver(
                new ProjectApplicationWorkspaceScope(
                    "project.main",
                    "application.main",
                    projectPath,
                    ApplicationProjectPath),
                calls),
            new RecordingSourceResolver(Result.Success(CreateMetadata()), calls),
            new RecordingArtifactStore(CreateArtifact(projectPath), calls),
            new FailingStationPackagePublisher(calls),
            new FixedClock(PublishedAtUtc));

        var result = await publisher.PublishAsync(
            "project.main",
            new PublishProjectReleaseRequest("snapshot.main", "application.main", "line.main"));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.StationPackagePublicationFailed", result.Error.Code);
        Assert.Contains("artifact.rollback", calls);
        Assert.DoesNotContain("project.publish", calls);
        Assert.Null(projectService.PublishRequest);
    }

    [Fact]
    public async Task CancellationBeforeRootManifestCommitRestoresAggregateAndAllArtifacts()
    {
        var calls = new List<string>();
        var projectPath = Path.Combine(Path.GetTempPath(), $"openlineops-cancel-rollback-{Guid.NewGuid():N}");
        var previousProject = CreateProject(projectPath);
        var projectService = new RecordingProjectService(previousProject, calls);
        var publisher = new ProjectReleasePublisher(
            projectService,
            new CancelingManifestWorkspaceService(previousProject, projectService, calls),
            new RecordingScopeResolver(
                new ProjectApplicationWorkspaceScope(
                    "project.main",
                    "application.main",
                    projectPath,
                    ApplicationProjectPath),
                calls),
            new RecordingSourceResolver(Result.Success(CreateMetadata()), calls),
            new RecordingArtifactStore(CreateArtifact(projectPath), calls),
            new RecordingStationPackagePublisher(calls),
            new FixedClock(PublishedAtUtc));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await publisher.PublishAsync(
                "project.main",
                new PublishProjectReleaseRequest("snapshot.main", "application.main", "line.main")));

        Assert.Null(projectService.CurrentProject.ActiveSnapshotId);
        Assert.Empty(projectService.CurrentProject.Snapshots);
        Assert.Contains("workspace.open", calls);
        Assert.Contains("station-packages.rollback", calls);
        Assert.Contains("artifact.rollback", calls);
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
                    ["process.main"],
                    ApplicationProjectPath)
            ],
            Snapshots: []);
    }

    private static void WriteFrozenApplicationResources(string applicationRoot)
    {
        var topology = Path.Combine(applicationRoot, "topology");
        var layouts = Path.Combine(applicationRoot, "layouts");
        var line = Path.Combine(applicationRoot, "production", "lines", "line.main");
        var projects = Path.Combine(applicationRoot, "configuration", "projects");
        var profiles = Path.Combine(applicationRoot, "configuration", "station-profiles");
        Directory.CreateDirectory(topology);
        Directory.CreateDirectory(layouts);
        Directory.CreateDirectory(line);
        Directory.CreateDirectory(projects);
        Directory.CreateDirectory(profiles);
        File.WriteAllText(
            Path.Combine(topology, "topology.json"),
            "{\"schemaVersion\":\"openlineops.automation-topology\",\"resourceKind\":\"OpenLineOps.AutomationTopology\",\"applicationId\":\"application.main\",\"topologyId\":\"topology.main\"}");
        foreach (var layoutId in new[] { "layout.main", "layout.overview" })
        {
            File.WriteAllText(
                Path.Combine(layouts, $"{layoutId}.json"),
                $"{{\"schemaVersion\":\"openlineops.site-layout\",\"resourceKind\":\"OpenLineOps.SiteLayout\",\"applicationId\":\"application.main\",\"layoutId\":\"{layoutId}\"}}");
        }

        File.WriteAllText(
            Path.Combine(line, "line.json"),
            "{\"schemaVersion\":\"openlineops.production-line\",\"resourceKind\":\"OpenLineOps.ProductionLine\",\"applicationId\":\"application.main\",\"lineDefinitionId\":\"line.main\"}");
        File.WriteAllText(
            Path.Combine(projects, "project-main.json"),
            "{\"schema\":\"openlineops.engineering-configuration-resource\",\"schemaVersion\":1,\"applicationId\":\"application.main\",\"resourceKind\":\"project\",\"resourceId\":\"engineering.main\",\"snapshot\":{\"projectId\":\"engineering.main\",\"workspaceId\":\"workspace.main\",\"displayName\":\"Main\",\"createdAtUtc\":\"2026-07-10T08:00:00+00:00\",\"activeSnapshotId\":\"configuration.main.v1\",\"snapshots\":[{\"snapshotId\":\"configuration.main.v1\",\"projectId\":\"engineering.main\",\"processDefinitionId\":\"process.main\",\"processVersionId\":\"process.main@1.0.0\",\"recipeId\":\"recipe.main\",\"recipeVersionId\":\"recipe.main@1\",\"stationProfileId\":\"station.profile.main\",\"status\":\"Published\",\"publishedAtUtc\":\"2026-07-10T08:00:00+00:00\",\"deviceBindings\":[]}]}}");
        File.WriteAllText(
            Path.Combine(profiles, "station-profile-main.json"),
            "{\"schema\":\"openlineops.engineering-configuration-resource\",\"schemaVersion\":1,\"applicationId\":\"application.main\",\"resourceKind\":\"station-profile\",\"resourceId\":\"station.profile.main\",\"snapshot\":{\"stationProfileId\":\"station.profile.main\",\"stationSystemId\":\"station.eol\",\"displayName\":\"EOL\",\"deviceBindings\":[]}}");
    }

    private static ProjectReleaseSourceMetadata CreateMetadata()
    {
        return new ProjectReleaseSourceMetadata(
            "topology.main",
            ["layout.main", "layout.overview"],
            CreateProductionMetadata(),
            [],
            [
                new ProjectReleaseCapabilityBinding(
                    "motion.axis",
                    "binding.motion",
                    "Simulator",
                    "simulator.motion",
                    "station.main",
                    "station.main")
            ],
            [new ProjectReleaseTargetReference("Slot", "slot.main")],
            ["openlineops_move_axis@1"],
            []);
    }

    private static ProjectReleaseSourceMetadata CreateMetadataWithChangedFlowIr()
    {
        var metadata = CreateMetadata();
        var operation = Assert.Single(metadata.ProductionLine.Operations) with
        {
            FlowIrSha256 = new string('b', 64)
        };
        return metadata with
        {
            ProductionLine = metadata.ProductionLine with { Operations = [operation] }
        };
    }

    private static ProjectReleaseProductionLine CreateProductionMetadata()
    {
        return new ProjectReleaseProductionLine(
            "line.main",
            "Main Line",
            "topology.main",
            new ProjectReleaseProductModel("product.main", "MAINBOARD-A", "serialNumber"),
            "operation.eol",
            [
                new ProjectReleaseOperation(
                    "operation.eol",
                    "EOL",
                    "station.eol",
                    "process.main",
                    "configuration.main.v1",
                    "process.main@1.0.0",
                    "openlineops.flow-ir",
                    "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                    "{}",
                    ["openlineops_move_axis@1"],
                    [new ProjectReleaseOperationResource(
                        "resource.station",
                        "Station",
                        "station.eol",
                        "Fixed",
                        [])],
                    [])
            ],
            Transitions:
            [
                new ProjectReleaseRouteTransition(
                    "operation.eol.completed",
                    "operation.eol",
                    null,
                    "Completed",
                    "Sequence",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)
            ],
            LineControllerAuthorizations: []);
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
            ApplicationProjectPath,
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

        public string? ProductionLineDefinitionId { get; private set; }

        public Task<Result<ProjectReleaseSourceMetadata>> ResolveAsync(
            ProjectApplicationWorkspaceScope scope,
            string topologyId,
            string productionLineDefinitionId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("source.resolve");
            _invocationCount += 1;
            TopologyId = topologyId;
            ProductionLineDefinitionId = productionLineDefinitionId;
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

        public ValueTask RollbackPublicationAsync(
            ProjectApplicationWorkspaceScope scope,
            string snapshotId,
            string expectedContentSha256,
            CancellationToken cancellationToken = default)
        {
            calls.Add("artifact.rollback");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStationPackagePublisher(List<string> calls)
        : IProjectReleaseStationPackagePublisher
    {
        public ValueTask ValidateConfigurationAsync(
            ProjectReleaseStationPackagePreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("station-packages.validate");
            return ValueTask.CompletedTask;
        }

        public ValueTask<ProjectReleaseStationPackageSet> PublishAsync(
            ProjectReleaseStationPackageRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("station-packages.publish");
            var packages = request.Metadata.ProductionLine.Operations
                .Select(operation => operation.StationSystemId)
                .Distinct(StringComparer.Ordinal)
                .Select(stationSystemId => new ProjectReleaseStationPackage(
                    stationSystemId,
                    new string('b', 64),
                    Path.Combine(request.Release.ReleaseRootPath, $"{stationSystemId}.olopkg"),
                    Path.Combine(request.Release.ReleaseRootPath, $"{stationSystemId}.json")))
                .ToArray();
            return ValueTask.FromResult(new ProjectReleaseStationPackageSet(
                request.Release.ProjectId,
                request.Release.ApplicationId,
                request.Release.SnapshotId,
                packages));
        }

        public ValueTask RollbackAsync(
            ProjectReleaseStationPackageSet packages,
            CancellationToken cancellationToken = default)
        {
            calls.Add("station-packages.rollback");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStationPackagePublisher(List<string> calls)
        : IProjectReleaseStationPackagePublisher
    {
        public ValueTask ValidateConfigurationAsync(
            ProjectReleaseStationPackagePreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("station-packages.validate");
            return ValueTask.CompletedTask;
        }

        public ValueTask<ProjectReleaseStationPackageSet> PublishAsync(
            ProjectReleaseStationPackageRequest request,
            CancellationToken cancellationToken = default)
        {
            calls.Add("station-packages.publish");
            return ValueTask.FromException<ProjectReleaseStationPackageSet>(
                new IOException("Distribution write failed."));
        }

        public ValueTask RollbackAsync(
            ProjectReleaseStationPackageSet packages,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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

        public Task<Result<AutomationProjectWorkspaceDetails>> ImportApplicationAsync(
            string projectId,
            ImportAutomationProjectApplicationRequest request,
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

        public Task<Result<AutomationProjectWorkspaceDetails>> ImportApplicationAsync(
            string projectId,
            ImportAutomationProjectApplicationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CancelingManifestWorkspaceService(
        AutomationProjectDetails previousProject,
        RecordingProjectService projectService,
        List<string> calls) : IAutomationProjectWorkspaceService
    {
        private int _saveCount;

        public Task<Result<AutomationProjectWorkspaceDetails>> SaveManifestAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("workspace.save");
            _saveCount += 1;
            return _saveCount == 1
                ? Task.FromResult(Result.Success(CreateWorkspaceDetails(previousProject)))
                : Task.FromException<Result<AutomationProjectWorkspaceDetails>>(
                    new OperationCanceledException("Canceled before root manifest commit."));
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> OpenAsync(
            OpenAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(CancellationToken.None, cancellationToken);
            calls.Add("workspace.open");
            projectService.Restore(previousProject);
            return Task.FromResult(Result.Success(CreateWorkspaceDetails(previousProject)));
        }

        public Task<Result<AutomationProjectWorkspaceDetails>> CreateAsync(
            CreateAutomationProjectWorkspaceRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<AutomationProjectWorkspaceDetails>> ImportApplicationAsync(
            string projectId,
            ImportAutomationProjectApplicationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
