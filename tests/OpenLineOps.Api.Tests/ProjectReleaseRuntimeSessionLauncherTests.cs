using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Sessions;
using RuntimeSessionId = OpenLineOps.Runtime.Domain.Identifiers.RuntimeSessionId;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectReleaseRuntimeSessionLauncherTests
{
    private static readonly DateTimeOffset PublishedAtUtc =
        new(2026, 7, 10, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartRejectsReleaseMetadataMismatchBeforeReadingReleaseSources()
    {
        var processRepository = new RecordingProcessRepository(null);
        var configurationResolver = new RecordingConfigurationResolver(
            Result.Failure<RuntimeConfigurationSnapshotDetails>(
                ApplicationError.NotFound("Tests.NotFound", "Not found.")));
        var release = OpenedRelease() with
        {
            Metadata = OpenedRelease().Metadata with { TopologyId = "topology.other" }
        };
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            processRepository,
            configurationResolver,
            new RecordingRuntimeSessionRunner());

        var result = await launcher.StartAsync(Snapshot(), StartRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseMetadataMismatch", result.Error.Code);
        Assert.Equal(0, processRepository.GetCallCount);
        Assert.Equal(0, configurationResolver.CallCount);
    }

    [Fact]
    public async Task StartRejectsReleaseManifestPathMismatch()
    {
        var processRepository = new RecordingProcessRepository(null);
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(OpenedRelease()),
            processRepository,
            new RecordingConfigurationResolver(Result.Failure<RuntimeConfigurationSnapshotDetails>(
                ApplicationError.NotFound("Tests.NotFound", "Not found."))),
            new RecordingRuntimeSessionRunner());

        var result = await launcher.StartAsync(
            Snapshot(releaseManifestPath: "releases/release-other/release.json"),
            StartRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseMetadataMismatch", result.Error.Code);
        Assert.Contains("release manifest path", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, processRepository.GetCallCount);
    }

    [Fact]
    public async Task StartLoadsAndRunsOnlyFromOpenedReleaseSourceRoot()
    {
        var definition = PublishedProcessDefinition();
        var processRepository = new RecordingProcessRepository(definition);
        var configurationResolver = new RecordingConfigurationResolver(Result.Success(
            new RuntimeConfigurationSnapshotDetails(
                "configuration.main",
                "process.main",
                "process.main@1.0.0",
                "recipe.main@1.0.0",
                "station.main")));
        var runner = new RecordingRuntimeSessionRunner();
        var release = OpenedRelease();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            processRepository,
            configurationResolver,
            runner);

        var result = await launcher.StartAsync(Snapshot(), StartRequest());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.NotNull(processRepository.LastScope);
        Assert.NotNull(configurationResolver.LastScope);
        Assert.Equal(Path.GetFullPath(release.SourceRootPath), processRepository.LastScope.ProjectPath);
        Assert.Equal(Path.GetFullPath(release.SourceRootPath), configurationResolver.LastScope.ProjectPath);
        Assert.Equal("project.main", processRepository.LastScope.ProjectId);
        Assert.Equal("application.main", processRepository.LastScope.ApplicationId);
        var runtimeRequest = Assert.IsType<StartRuntimeSessionRequest>(runner.LastRequest);
        var traceMetadata = Assert.IsType<RuntimeSessionTraceMetadata>(runtimeRequest.TraceMetadata);
        Assert.Equal("project.main", traceMetadata.ProjectId);
        Assert.Equal("application.main", traceMetadata.ApplicationId);
        Assert.Equal("snapshot.main", traceMetadata.ProjectSnapshotId);
        Assert.Equal("topology.main", traceMetadata.TopologyId);
        Assert.Equal("process.main@1.0.0", runtimeRequest.Process.ProcessVersionId.Value);
        Assert.Equal("MoveAbsolute", Assert.Single(runtimeRequest.Process.Nodes).CommandName);
        Assert.Equal("configuration.main", runtimeRequest.ConfigurationSnapshotId.Value);
    }

    [Fact]
    public async Task StartRejectsFrozenFlowIrHashMismatchBeforeConfigurationResolution()
    {
        var release = OpenedRelease() with
        {
            Metadata = OpenedRelease().Metadata with { FlowIrSha256 = new string('b', 64) }
        };
        var configurationResolver = new RecordingConfigurationResolver(Result.Success(
            new RuntimeConfigurationSnapshotDetails(
                "configuration.main",
                "process.main",
                "process.main@1.0.0",
                "recipe.main@1.0.0",
                "station.main")));
        var runner = new RecordingRuntimeSessionRunner();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            new RecordingProcessRepository(PublishedProcessDefinition()),
            configurationResolver,
            runner);

        var result = await launcher.StartAsync(Snapshot(), StartRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseFlowIrInvalid", result.Error.Code);
        Assert.Equal(0, configurationResolver.CallCount);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task StartExecutesFrozenFlowIrWhenCopiedSourceIdentityMatchesButContentDiffers()
    {
        var configurationResolver = new RecordingConfigurationResolver(Result.Success(
            new RuntimeConfigurationSnapshotDetails(
                "configuration.main",
                "process.main",
                "process.main@1.0.0",
                "recipe.main@1.0.0",
                "station.main")));
        var runner = new RecordingRuntimeSessionRunner();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(OpenedRelease()),
            new RecordingProcessRepository(PublishedProcessDefinition("ChangedAfterCompilerUpgrade")),
            configurationResolver,
            runner);

        var result = await launcher.StartAsync(Snapshot(), StartRequest());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        var runtimeRequest = Assert.IsType<StartRuntimeSessionRequest>(runner.LastRequest);
        Assert.Equal("MoveAbsolute", Assert.Single(runtimeRequest.Process.Nodes).CommandName);
    }

    private static ProjectReleaseRuntimeSessionLauncher CreateLauncher(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseArtifactStore releaseStore,
        IProjectProcessDefinitionRepository processRepository,
        IProjectRuntimeConfigurationSnapshotResolver configurationResolver,
        IRuntimeSessionRunner sessionRunner)
    {
        var serializer = new FlowIrCanonicalSerializer();
        return new ProjectReleaseRuntimeSessionLauncher(
            scopeResolver,
            releaseStore,
            processRepository,
            configurationResolver,
            sessionRunner,
            serializer,
            new FlowIrExecutableRuntimeProcessMapper(serializer));
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
            "process.main",
            "process.main@1.0.0",
            "configuration.main",
            PublishedAtUtc,
            [new SnapshotCapabilityBindingDetails(
                "motion.axis.move",
                "binding.axis.x",
                "Driver",
                "driver.axis.x")],
            [new ProjectTargetReferenceDetails("System", "station.main")],
            ["motion.move@1"],
            releaseManifestPath,
            releaseContentSha256);
    }

    private static OpenedProjectReleaseArtifact OpenedRelease()
    {
        var flowIr = FrozenFlowIr();
        var sourceRootPath = Path.Combine(
            LiveScope().ProjectPath,
            "releases",
            "release-snapshot.main",
            "source");
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
                "station.main",
                ["layout.main"],
                "process.main",
                "process.main@1.0.0",
                flowIr.SchemaVersion,
                flowIr.Sha256,
                flowIr.CanonicalJson,
                "configuration.main",
                [new ProjectReleaseCapabilityBinding(
                    "motion.axis.move",
                    "binding.axis.x",
                    "Driver",
                    "driver.axis.x")],
                [new ProjectReleaseTargetReference("System", "station.main")],
                ["motion.move@1"],
                []),
            []);
    }

    private static FlowIrCanonicalArtifact FrozenFlowIr()
    {
        var compilationResult = new ProcessFlowIrCompiler().Compile(PublishedProcessDefinition());
        Assert.True(compilationResult.IsSuccess, compilationResult.Error.Message);
        var artifactResult = new FlowIrCanonicalSerializer().Serialize(compilationResult.Value.Document);
        Assert.True(artifactResult.IsSuccess, artifactResult.Error.Message);
        return artifactResult.Value;
    }

    private static ProjectApplicationWorkspaceScope LiveScope()
    {
        return new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            Path.Combine(Path.GetTempPath(), "openlineops-live-runtime-tests"),
            "applications/application.main/application.main.oloapp");
    }

    private static StartProcessRuntimeSessionRequest StartRequest()
    {
        return new StartProcessRuntimeSessionRequest(
            "configuration.main",
            SerialNumber: "SN-1",
            BatchId: "batch-1",
            FixtureId: "fixture-1",
            DeviceId: "device-1",
            ActorId: "operator-1");
    }

    private static ProcessDefinition PublishedProcessDefinition(string commandName = "MoveAbsolute")
    {
        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId("process.main"),
            new ProcessVersionId("process.main@1.0.0"),
            "Main Process",
            PublishedAtUtc.AddMinutes(-1));
        Assert.True(definition.AddNode(ProcessNode.Start(new ProcessNodeId("start"), "Start")).Succeeded);
        Assert.True(definition.AddNode(ProcessNode.Command(
            new ProcessNodeId("move"),
            "Move",
            new ProcessCapabilityId("motion.axis.move"),
            commandName,
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

    private sealed class RecordingScopeResolver : IProjectApplicationWorkspaceScopeResolver
    {
        private readonly ProjectApplicationWorkspaceScope? _scope;

        public RecordingScopeResolver(ProjectApplicationWorkspaceScope? scope)
        {
            _scope = scope;
        }

        public int CallCount { get; private set; }

        public ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
            string projectId,
            string applicationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_scope);
        }
    }

    private sealed class RecordingReleaseStore : IProjectReleaseArtifactStore
    {
        private readonly OpenedProjectReleaseArtifact? _release;

        public RecordingReleaseStore(OpenedProjectReleaseArtifact? release)
        {
            _release = release;
        }

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
            return ValueTask.FromResult(_release);
        }
    }

    private sealed class RecordingProcessRepository : IProjectProcessDefinitionRepository
    {
        private readonly ProcessDefinition? _definition;

        public RecordingProcessRepository(ProcessDefinition? definition)
        {
            _definition = definition;
        }

        public int GetCallCount { get; private set; }

        public ProjectApplicationWorkspaceScope? LastScope { get; private set; }

        public ValueTask SaveAsync(
            ProjectApplicationWorkspaceScope scope,
            ProcessDefinition definition,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<ProcessDefinition?> GetByIdAsync(
            ProjectApplicationWorkspaceScope scope,
            ProcessDefinitionId definitionId,
            CancellationToken cancellationToken = default)
        {
            GetCallCount++;
            LastScope = scope;
            return ValueTask.FromResult(_definition);
        }

        public ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
            ProjectApplicationWorkspaceScope scope,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingConfigurationResolver : IProjectRuntimeConfigurationSnapshotResolver
    {
        private readonly Result<RuntimeConfigurationSnapshotDetails> _result;

        public RecordingConfigurationResolver(Result<RuntimeConfigurationSnapshotDetails> result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public ProjectApplicationWorkspaceScope? LastScope { get; private set; }

        public ValueTask<Result<RuntimeConfigurationSnapshotDetails>> ResolveAsync(
            ProjectApplicationWorkspaceScope scope,
            string configurationSnapshotId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastScope = scope;
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class RecordingRuntimeSessionRunner : IRuntimeSessionRunner
    {
        public StartRuntimeSessionRequest? LastRequest { get; private set; }

        public ValueTask<Result<RuntimeSessionRunResult>> RunAsync(
            StartRuntimeSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(Result.Success(new RuntimeSessionRunResult(
                new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000123")),
                request.ConfigurationSnapshotId,
                RuntimeSessionStatus.Completed,
                CompletedSteps: 1,
                CommandCount: 1,
                IncidentCount: 0)));
        }
    }
}
