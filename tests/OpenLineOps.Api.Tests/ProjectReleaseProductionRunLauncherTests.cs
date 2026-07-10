using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectReleaseProductionRunLauncherTests
{
    private static readonly DateTimeOffset PublishedAtUtc =
        new(2026, 7, 10, 8, 30, 0, TimeSpan.Zero);

    private static readonly Guid RunId =
        Guid.Parse("00000000-0000-0000-0000-000000000123");

    [Fact]
    public async Task StartRejectsReleaseMetadataMismatchBeforeResolvingStageConfigurations()
    {
        var configurationResolver = new RecordingConfigurationResolver([]);
        var release = OpenedRelease() with
        {
            Metadata = OpenedRelease().Metadata with { TopologyId = "topology.other" }
        };
        var runner = new RecordingProductionRunRunner();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            configurationResolver,
            runner);

        var result = await launcher.StartAsync(Snapshot(), StartRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseMetadataMismatch", result.Error.Code);
        Assert.Equal(0, configurationResolver.CallCount);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task StartRejectsReleaseManifestPathMismatch()
    {
        var configurationResolver = new RecordingConfigurationResolver([]);
        var runner = new RecordingProductionRunRunner();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(OpenedRelease()),
            configurationResolver,
            runner);

        var result = await launcher.StartAsync(
            Snapshot(releaseManifestPath: "releases/release-other/release.json"),
            StartRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseMetadataMismatch", result.Error.Code);
        Assert.Contains("release manifest path", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, configurationResolver.CallCount);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task StartBuildsEveryStageOnlyFromTheOpenedImmutableRelease()
    {
        var release = OpenedRelease(stageCount: 2);
        var configurationResolver = new RecordingConfigurationResolver(
        [
            RuntimeConfiguration("configuration.main"),
            RuntimeConfiguration("configuration.secondary")
        ]);
        var runner = new RecordingProductionRunRunner();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            configurationResolver,
            runner);

        var result = await launcher.StartAsync(Snapshot(), StartRequest());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.NotNull(configurationResolver.LastScope);
        Assert.Equal(Path.GetFullPath(release.SourceRootPath), configurationResolver.LastScope.ProjectPath);
        Assert.Equal("project.main", configurationResolver.LastScope.ProjectId);
        Assert.Equal("application.main", configurationResolver.LastScope.ApplicationId);
        Assert.Equal(
            ["configuration.main", "configuration.secondary"],
            configurationResolver.RequestedConfigurationIds);

        var runRequest = Assert.IsType<StartProductionRunRequest>(runner.LastRequest);
        Assert.Equal(RunId, runRequest.RunId.Value);
        Assert.Equal("project.main", runRequest.ProjectId);
        Assert.Equal("application.main", runRequest.ApplicationId);
        Assert.Equal("snapshot.main", runRequest.ProjectSnapshotId);
        Assert.Equal("topology.main", runRequest.TopologyId);
        Assert.Equal("line.main", runRequest.ProductionLineDefinitionId);
        Assert.Equal("dut.main", runRequest.DutIdentity.ModelId);
        Assert.Equal("serialNumber", runRequest.DutIdentity.InputKey);
        Assert.Equal("DUT-1", runRequest.DutIdentity.Value);
        Assert.Equal("operator-1", runRequest.ActorId);
        Assert.Equal(2, runRequest.Stages.Count);
        Assert.Collection(
            runRequest.Stages,
            stage => AssertStage(stage, "stage.main", 1, "configuration.main"),
            stage => AssertStage(stage, "stage.secondary", 2, "configuration.secondary"));
    }

    [Fact]
    public async Task StartRejectsFrozenStageFlowHashMismatchBeforeConfigurationResolution()
    {
        var release = OpenedRelease();
        var stage = Assert.Single(release.Metadata.ProductionLine.Stages);
        release = release with
        {
            Metadata = release.Metadata with
            {
                ProductionLine = release.Metadata.ProductionLine with
                {
                    Stages = [stage with { FlowIrSha256 = new string('b', 64) }]
                }
            }
        };
        var configurationResolver = new RecordingConfigurationResolver([]);
        var runner = new RecordingProductionRunRunner();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            configurationResolver,
            runner);

        var result = await launcher.StartAsync(Snapshot(), StartRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseFlowIrIdentityMismatch", result.Error.Code);
        Assert.Equal(0, configurationResolver.CallCount);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task StartRejectsPaddedDutIdentityWithoutOpeningRelease()
    {
        var releaseStore = new RecordingReleaseStore(OpenedRelease());
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            releaseStore,
            new RecordingConfigurationResolver([]),
            new RecordingProductionRunRunner());

        var result = await launcher.StartAsync(
            Snapshot(),
            StartRequest() with { DutIdentityValue = " DUT-1" });

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Projects.ProductionRunIdentityInvalid", result.Error.Code);
        Assert.Equal(0, releaseStore.OpenCallCount);
    }

    [Fact]
    public async Task StartRejectsProjectAlreadyOwnedByIdeOrRunnerBeforeOpeningRelease()
    {
        var releaseStore = new RecordingReleaseStore(OpenedRelease());
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            releaseStore,
            new RecordingConfigurationResolver([]),
            new RecordingProductionRunRunner(),
            new StubProjectExecutionCoordinator(isAvailable: false));

        var result = await launcher.StartAsync(Snapshot(), StartRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectExecutionAlreadyActive", result.Error.Code);
        Assert.Equal(0, releaseStore.OpenCallCount);
    }

    private static void AssertStage(
        ProductionStageExecutionPlan stage,
        string stageId,
        int sequence,
        string configurationSnapshotId)
    {
        Assert.Equal(stageId, stage.StageId);
        Assert.Equal(sequence, stage.Sequence);
        Assert.Equal("workstation.main", stage.WorkstationId);
        Assert.Equal("station.main", stage.StationId.Value);
        Assert.Equal(configurationSnapshotId, stage.ConfigurationSnapshotId.Value);
        Assert.Equal("process.main@1.0.0", stage.FrozenExecutableProcess.ProcessVersionId.Value);
        Assert.Equal("MoveAbsolute", Assert.Single(stage.FrozenExecutableProcess.Nodes).CommandName);
    }

    private static ProjectReleaseProductionRunLauncher CreateLauncher(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseArtifactStore releaseStore,
        IProjectRuntimeConfigurationSnapshotResolver configurationResolver,
        IProductionRunRunner productionRunRunner,
        IProjectExecutionCoordinator? executionCoordinator = null)
    {
        var serializer = new FlowIrCanonicalSerializer();
        return new ProjectReleaseProductionRunLauncher(
            scopeResolver,
            releaseStore,
            configurationResolver,
            executionCoordinator ?? new StubProjectExecutionCoordinator(isAvailable: true),
            productionRunRunner,
            serializer,
            new FlowIrExecutableRuntimeProcessMapper(serializer));
    }

    private sealed class StubProjectExecutionCoordinator(bool isAvailable)
        : IProjectExecutionCoordinator
    {
        public ValueTask<IProjectExecutionLease?> TryAcquireAsync(
            string projectDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IProjectExecutionLease?>(
                isAvailable ? new StubProjectExecutionLease() : null);
        }
    }

    private sealed class StubProjectExecutionLease : IProjectExecutionLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static PublishedProjectSnapshotDetails Snapshot(
        string releaseManifestPath = "releases/release-snapshot.main/release.json",
        string releaseContentSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        return new PublishedProjectSnapshotDetails(
            "snapshot.main",
            "project.main",
            "application.main",
            "topology.main",
            ["layout.main"],
            "line.main",
            PublishedAtUtc,
            [new SnapshotCapabilityBindingDetails(
                "motion.axis.move",
                "binding.axis.x",
                "Driver",
                "driver.axis.x")],
            [new ProjectTargetReferenceDetails("System", "station.main")],
            [],
            releaseManifestPath,
            releaseContentSha256);
    }

    private static OpenedProjectReleaseArtifact OpenedRelease(int stageCount = 1)
    {
        var flowIr = FrozenFlowIr();
        var sourceRootPath = Path.Combine(
            LiveScope().ProjectPath,
            "releases",
            "release-snapshot.main",
            "source");
        var stages = new List<ProjectReleaseProductionStage>
        {
            ProductionStage(
                "stage.main",
                1,
                "configuration.main",
                flowIr)
        };
        if (stageCount == 2)
        {
            stages.Add(ProductionStage(
                "stage.secondary",
                2,
                "configuration.secondary",
                flowIr));
        }

        return new OpenedProjectReleaseArtifact(
            "snapshot.main",
            "project.main",
            "application.main",
            PublishedAtUtc,
            new string('a', 64),
            Path.GetDirectoryName(sourceRootPath)!,
            sourceRootPath,
            "applications/application.main/application.main.oloapp",
            Path.Combine(Path.GetDirectoryName(sourceRootPath)!, "release.json"),
            new ProjectReleaseSourceMetadata(
                "topology.main",
                ["layout.main"],
                new ProjectReleaseProductionLine(
                    "line.main",
                    "Main Line",
                    "topology.main",
                    new ProjectReleaseDutModel("dut.main", "MAINBOARD-A", "serialNumber"),
                    [new ProjectReleaseWorkstation("workstation.main", "Main", "station.main")],
                    stages,
                    []),
                [new ProjectReleaseCapabilityBinding(
                    "motion.axis.move",
                    "binding.axis.x",
                    "Driver",
                    "driver.axis.x")],
                [new ProjectReleaseTargetReference("System", "station.main")],
                [],
                []),
            []);
    }

    private static ProjectReleaseProductionStage ProductionStage(
        string stageId,
        int sequence,
        string configurationSnapshotId,
        FlowIrCanonicalArtifact flowIr)
    {
        return new ProjectReleaseProductionStage(
            stageId,
            sequence,
            stageId,
            "workstation.main",
            "process.main",
            configurationSnapshotId,
            "process.main@1.0.0",
            flowIr.SchemaVersion,
            flowIr.Sha256,
            flowIr.CanonicalJson,
            [],
            ExternalTestProgramAdapterId: null);
    }

    private static FlowIrCanonicalArtifact FrozenFlowIr()
    {
        var compilationResult = new ProcessFlowIrCompiler().Compile(PublishedProcessDefinition());
        Assert.True(compilationResult.IsSuccess, compilationResult.Error.Message);
        var artifactResult = new FlowIrCanonicalSerializer().Serialize(compilationResult.Value.Document);
        Assert.True(artifactResult.IsSuccess, artifactResult.Error.Message);
        return artifactResult.Value;
    }

    private static RuntimeConfigurationSnapshotDetails RuntimeConfiguration(string id)
    {
        return new RuntimeConfigurationSnapshotDetails(
            id,
            "process.main",
            "process.main@1.0.0",
            "recipe.main@1.0.0",
            "station.main");
    }

    private static ProjectApplicationWorkspaceScope LiveScope()
    {
        return new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            Path.Combine(Path.GetTempPath(), "openlineops-live-production-run-tests"),
            "applications/application.main/application.main.oloapp");
    }

    private static StartProjectReleaseProductionRunRequest StartRequest()
    {
        return new StartProjectReleaseProductionRunRequest(
            RunId,
            "DUT-1",
            "operator-1",
            "batch-1",
            "fixture-1",
            "device-1");
    }

    private static ProcessDefinition PublishedProcessDefinition()
    {
        var definition = ProcessDefinition.Create(
            new OpenLineOps.Processes.Domain.Identifiers.ProcessDefinitionId("process.main"),
            new OpenLineOps.Processes.Domain.Identifiers.ProcessVersionId("process.main@1.0.0"),
            "Main Process",
            PublishedAtUtc.AddMinutes(-1));
        Assert.True(definition.AddNode(ProcessNode.Start(new ProcessNodeId("start"), "Start")).Succeeded);
        Assert.True(definition.AddNode(ProcessNode.Command(
            new ProcessNodeId("move"),
            "Move",
            new ProcessCapabilityId("motion.axis.move"),
            ProcessActionTargetKind.Capability,
            "motion.axis.move",
            "MoveAbsolute",
            TimeSpan.FromSeconds(5))).Succeeded);
        Assert.True(definition.AddNode(ProcessNode.End(new ProcessNodeId("end"), "End")).Succeeded);
        Assert.True(definition.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId("start-move"),
            new ProcessNodeId("start"),
            new ProcessNodeId("move"))).Succeeded);
        Assert.True(definition.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId("move-end"),
            new ProcessNodeId("move"),
            new ProcessNodeId("end"))).Succeeded);
        Assert.True(definition.Publish(PublishedAtUtc).Succeeded);
        return definition;
    }

    private sealed class RecordingScopeResolver(ProjectApplicationWorkspaceScope? scope)
        : IProjectApplicationWorkspaceScopeResolver
    {
        public ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
            string projectId,
            string applicationId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(scope);
        }
    }

    private sealed class RecordingReleaseStore(OpenedProjectReleaseArtifact? release)
        : IProjectReleaseArtifactStore
    {
        public int OpenCallCount { get; private set; }

        public ValueTask<ProjectReleaseArtifactDescriptor> PublishAsync(
            ProjectApplicationWorkspaceScope scope,
            string snapshotId,
            DateTimeOffset publishedAtUtc,
            ProjectReleaseSourceMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<OpenedProjectReleaseArtifact?> OpenAsync(
            ProjectApplicationWorkspaceScope scope,
            string snapshotId,
            string expectedContentSha256,
            CancellationToken cancellationToken = default)
        {
            OpenCallCount++;
            return ValueTask.FromResult(release);
        }
    }

    private sealed class RecordingConfigurationResolver(
        IReadOnlyCollection<RuntimeConfigurationSnapshotDetails> configurations)
        : IProjectRuntimeConfigurationSnapshotResolver
    {
        public int CallCount { get; private set; }

        public ProjectApplicationWorkspaceScope? LastScope { get; private set; }

        public List<string> RequestedConfigurationIds { get; } = [];

        public ValueTask<Result<RuntimeConfigurationSnapshotDetails>> ResolveAsync(
            ProjectApplicationWorkspaceScope scope,
            string configurationSnapshotId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastScope = scope;
            RequestedConfigurationIds.Add(configurationSnapshotId);
            var configuration = configurations.SingleOrDefault(candidate => string.Equals(
                candidate.ConfigurationSnapshotId,
                configurationSnapshotId,
                StringComparison.Ordinal));
            return ValueTask.FromResult(configuration is null
                ? Result.Failure<RuntimeConfigurationSnapshotDetails>(
                    ApplicationError.NotFound("Tests.ConfigurationNotFound", "Not found."))
                : Result.Success(configuration));
        }
    }

    private sealed class RecordingProductionRunRunner : IProductionRunRunner
    {
        public StartProductionRunRequest? LastRequest { get; private set; }

        public ValueTask<Result<ProductionRunRunResult>> RunAsync(
            StartProductionRunRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var timestamp = PublishedAtUtc.AddMinutes(1);
            var stages = request.Stages.Select(stage => new ProductionStageRunSnapshot(
                stage.StageId,
                stage.Sequence,
                stage.WorkstationId,
                stage.StationId,
                stage.FrozenExecutableProcess.ProcessDefinitionId,
                stage.FrozenExecutableProcess.ProcessVersionId,
                stage.ConfigurationSnapshotId,
                stage.RecipeSnapshotId,
                ProductionStageRunStatus.Completed,
                RuntimeSessionId.New(),
                timestamp,
                timestamp,
                FailureCode: null,
                FailureReason: null,
                CompletedStepCount: 1,
                CommandCount: 1,
                IncidentCount: 0)).ToArray();
            var snapshot = new ProductionRunSnapshot(
                request.RunId,
                request.ProjectId,
                request.ApplicationId,
                request.ProjectSnapshotId,
                request.TopologyId,
                request.ProductionLineDefinitionId,
                request.DutIdentity,
                request.BatchId,
                request.FixtureId,
                request.DeviceId,
                request.ActorId,
                ProductionRunStatus.Completed,
                timestamp,
                timestamp,
                timestamp,
                timestamp,
                FailureCode: null,
                FailureReason: null,
                stages);
            return ValueTask.FromResult(Result.Success(new ProductionRunRunResult(snapshot)));
        }
    }
}
