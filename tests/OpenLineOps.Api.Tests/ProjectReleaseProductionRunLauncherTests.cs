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
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectReleaseProductionRunLauncherTests
{
    private static readonly DateTimeOffset PublishedAtUtc =
        new(2026, 7, 10, 8, 30, 0, TimeSpan.Zero);

    private static readonly Guid RunId =
        Guid.Parse("00000000-0000-0000-0000-000000000123");
    private static readonly Guid ProductionUnitId =
        Guid.Parse("00000000-0000-0000-0000-000000000124");

    [Fact]
    public async Task SubmitRejectsReleaseMetadataMismatchBeforeResolvingConfigurations()
    {
        var configurationResolver = new RecordingConfigurationResolver([]);
        var release = OpenedRelease() with
        {
            Metadata = OpenedRelease().Metadata with { TopologyId = "topology.other" }
        };
        var coordinator = new RecordingProductionRunCoordinator();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            configurationResolver,
            coordinator);

        var result = await launcher.SubmitAsync(Snapshot(), SubmitRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseMetadataMismatch", result.Error.Code);
        Assert.Equal(0, configurationResolver.CallCount);
        Assert.Null(coordinator.LastRequest);
    }

    [Fact]
    public async Task SubmitRejectsReleaseManifestPathMismatch()
    {
        var coordinator = new RecordingProductionRunCoordinator();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(OpenedRelease()),
            new RecordingConfigurationResolver([]),
            coordinator);

        var result = await launcher.SubmitAsync(
            Snapshot(releaseManifestPath: "releases/release-other/release.json"),
            SubmitRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseMetadataMismatch", result.Error.Code);
        Assert.Contains("release manifest path", result.Error.Message, StringComparison.Ordinal);
        Assert.Null(coordinator.LastRequest);
    }

    [Fact]
    public async Task SubmitRejectsDuplicatePublishedSnapshotFrozenIdentities()
    {
        var snapshot = Snapshot();
        var binding = Assert.Single(snapshot.CapabilityBindings);
        var releaseStore = new RecordingReleaseStore(OpenedRelease());
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            releaseStore,
            new RecordingConfigurationResolver([]),
            new RecordingProductionRunCoordinator());

        var result = await launcher.SubmitAsync(
            snapshot with { CapabilityBindings = [binding, binding] },
            SubmitRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseMetadataMismatch", result.Error.Code);
        Assert.Contains("duplicate frozen identities", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitRejectsDuplicateReleaseFrozenIdentities()
    {
        var release = OpenedRelease();
        var target = release.Metadata.TargetReferences.First();
        release = release with
        {
            Metadata = release.Metadata with
            {
                TargetReferences = [.. release.Metadata.TargetReferences, target]
            }
        };
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            new RecordingConfigurationResolver([]),
            new RecordingProductionRunCoordinator());

        var result = await launcher.SubmitAsync(Snapshot(), SubmitRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseMetadataMismatch", result.Error.Code);
        Assert.Contains("duplicate frozen identities", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitBuildsPortableGraphAndFencedResourcesFromImmutableRelease()
    {
        var release = OpenedRelease(operationCount: 2, includeCondition: true);
        var configurationResolver = new RecordingConfigurationResolver(
        [
            RuntimeConfiguration("configuration.main", "station.main"),
            RuntimeConfiguration("configuration.secondary", "station.secondary")
        ]);
        var coordinator = new RecordingProductionRunCoordinator();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            configurationResolver,
            coordinator);

        var result = await launcher.SubmitAsync(Snapshot(), SubmitRequest());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(Path.GetFullPath(release.SourceRootPath), configurationResolver.LastScope?.ProjectPath);
        Assert.Equal(
            ["configuration.main", "configuration.secondary"],
            configurationResolver.RequestedConfigurationIds);

        var request = Assert.IsType<SubmitProductionRunRequest>(coordinator.LastRequest);
        Assert.Equal(RunId, request.RunId.Value);
        Assert.Equal("project.main", request.ProjectId);
        Assert.Equal("application.main", request.ApplicationId);
        Assert.Equal("snapshot.main", request.ProjectSnapshotId);
        Assert.Equal("topology.main", request.TopologyId);
        Assert.Equal("line.main", request.ProductionLineDefinitionId);
        Assert.Equal(ProductionUnitId, request.ProductionUnitId.Value);
        Assert.Equal("product.main", request.FrozenProductModelId);
        Assert.Equal("serialNumber", request.FrozenIdentityInputKey);
        Assert.Equal("operation.main", request.EntryOperationId);
        Assert.Equal(2, request.Operations.Count);

        Assert.Collection(
            request.Operations,
            operation => AssertOperation(operation, "operation.main", "station.main", "configuration.main"),
            operation => AssertOperation(
                operation,
                "operation.secondary",
                "station.secondary",
                "configuration.secondary"));

        Assert.Equal(3, request.RouteTransitions.Count);
        var transition = Assert.Single(request.RouteTransitions, candidate =>
            candidate.Kind == RuntimeRouteTransitionKind.Condition);
        Assert.Equal(RuntimeRouteTransitionKind.Condition, transition.Kind);
        Assert.Equal("inspection.accepted", transition.OutputCondition?.OutputKey);
        Assert.Equal(
            new ProductionContextValue(ProductionContextValueKind.Boolean, "true"),
            transition.OutputCondition?.ExpectedValue);
    }

    [Fact]
    public async Task SubmitRejectsFrozenOperationFlowHashMismatchBeforeConfigurationResolution()
    {
        var release = OpenedRelease();
        var operation = Assert.Single(release.Metadata.ProductionLine.Operations);
        release = release with
        {
            Metadata = release.Metadata with
            {
                ProductionLine = release.Metadata.ProductionLine with
                {
                    Operations = [operation with { FlowIrSha256 = new string('b', 64) }]
                }
            }
        };
        var configurationResolver = new RecordingConfigurationResolver([]);
        var coordinator = new RecordingProductionRunCoordinator();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            configurationResolver,
            coordinator);

        var result = await launcher.SubmitAsync(Snapshot(), SubmitRequest());

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Projects.ProjectReleaseFlowIrIdentityMismatch", result.Error.Code);
        Assert.Equal(0, configurationResolver.CallCount);
        Assert.Null(coordinator.LastRequest);
    }

    [Fact]
    public async Task SubmitCreatesOnlyFrozenFixedLeasesAndMaterialSlotPolicy()
    {
        var release = OpenedRelease();
        var operation = Assert.Single(release.Metadata.ProductionLine.Operations);
        release = release with
        {
            Metadata = release.Metadata with
            {
                ProductionLine = release.Metadata.ProductionLine with
                {
                    Operations = [operation with
                    {
                        Resources =
                        [
                            .. operation.Resources,
                            new ProjectReleaseOperationResource(
                                "resource.fixture",
                                "Fixture",
                                "fixture.main",
                                "Fixed",
                                []),
                            new ProjectReleaseOperationResource(
                                "resource.device",
                                "Device",
                                "device.main",
                                "Fixed",
                                []),
                            new ProjectReleaseOperationResource(
                                "resource.group",
                                "SlotGroup",
                                "group.main",
                                "Fixed",
                                []),
                            new ProjectReleaseOperationResource(
                                "resource.slot.fixed",
                                "Slot",
                                "slot.fixed",
                                "Fixed",
                                []),
                            new ProjectReleaseOperationResource(
                                "resource.slot.material",
                                "Slot",
                                "station.main",
                                "CurrentMaterialSlot",
                                [])
                        ]
                    }]
                }
            }
        };
        var coordinator = new RecordingProductionRunCoordinator();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            new RecordingConfigurationResolver(
                [RuntimeConfiguration("configuration.main", "station.main")]),
            coordinator);

        var result = await launcher.SubmitAsync(Snapshot(), SubmitRequest());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        var plan = Assert.Single(Assert.IsType<SubmitProductionRunRequest>(coordinator.LastRequest).Operations);
        Assert.Equal(
            [ResourceKind.Station, ResourceKind.Slot, ResourceKind.Fixture, ResourceKind.Device, ResourceKind.SlotGroup],
            plan.Definition.ResourceRequirements.Select(resource => resource.Kind));
        Assert.Contains(
            new ResourceRequirement(ResourceKind.Slot, "line.main/station.main/slot.fixed"),
            plan.Definition.ResourceRequirements);
        Assert.Equal(
            MaterialSlotResolution.CurrentMaterialSlot,
            plan.Definition.MaterialSlotRequirement?.Resolution);
        Assert.Equal("station.main", plan.Definition.MaterialSlotRequirement?.TopologyTargetId);
    }

    [Fact]
    public async Task SubmitAddsExactRemoteStationAndBindingLeasesForFrozenLineControllerGrant()
    {
        var release = OpenedRelease();
        var operation = Assert.Single(release.Metadata.ProductionLine.Operations);
        var authorizedAction = Assert.Single(operation.AuthorizedActions);
        var authorization = new ProjectReleaseLineControllerAuthorization(
            "authorization.remote-axis",
            operation.OperationId,
            authorizedAction.ActionId,
            "station.main",
            "binding.axis.x",
            authorizedAction.RequiredCapability,
            authorizedAction.CommandName,
            "station.remote",
            "system.remote-axis",
            "binding.remote-axis",
            "motion.remote-axis",
            "MoveRemote");
        release = release with
        {
            Metadata = release.Metadata with
            {
                ProductionLine = release.Metadata.ProductionLine with
                {
                    Operations = [operation with
                    {
                        AuthorizedActions = [authorizedAction with
                        {
                            TargetKind = "Driver",
                            TargetId = authorization.TargetBindingId,
                            LineControllerAuthorizationId = authorization.AuthorizationId
                        }]
                    }],
                    LineControllerAuthorizations = [authorization]
                }
            }
        };
        var coordinator = new RecordingProductionRunCoordinator();
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            new RecordingReleaseStore(release),
            new RecordingConfigurationResolver(
                [RuntimeConfiguration("configuration.main", "station.main")]),
            coordinator);

        var result = await launcher.SubmitAsync(Snapshot(), SubmitRequest());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        var plan = Assert.Single(Assert.IsType<SubmitProductionRunRequest>(coordinator.LastRequest).Operations);
        Assert.Contains(
            new ResourceRequirement(ResourceKind.Station, authorization.TargetStationSystemId),
            plan.Definition.ResourceRequirements);
        Assert.Contains(
            new ResourceRequirement(ResourceKind.Device, authorization.TargetBindingId),
            plan.Definition.ResourceRequirements);
    }

    [Fact]
    public async Task SubmitRejectsEmptyProductionUnitIdentityWithoutOpeningRelease()
    {
        var releaseStore = new RecordingReleaseStore(OpenedRelease());
        var launcher = CreateLauncher(
            new RecordingScopeResolver(LiveScope()),
            releaseStore,
            new RecordingConfigurationResolver([]),
            new RecordingProductionRunCoordinator());

        var result = await launcher.SubmitAsync(
            Snapshot(),
            SubmitRequest() with { ProductionUnitId = Guid.Empty });

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Projects.ProductionRunIdentityInvalid", result.Error.Code);
        Assert.Equal(0, releaseStore.OpenCallCount);
    }

    private static void AssertOperation(
        OperationExecutionPlan operation,
        string operationId,
        string stationSystemId,
        string configurationSnapshotId)
    {
        Assert.Equal(operationId, operation.Definition.OperationId);
        Assert.Equal(stationSystemId, operation.Definition.StationSystemId);
        Assert.Equal(stationSystemId, operation.Definition.StationId.Value);
        Assert.Equal(configurationSnapshotId, operation.Definition.ConfigurationSnapshotId.Value);
        Assert.Equal("process.main@1.0.0", operation.FrozenExecutableProcess.ProcessVersionId.Value);
        Assert.Equal("MoveAbsolute", Assert.Single(operation.FrozenExecutableProcess.Nodes).CommandName);
        Assert.Equal(
            [
                new ResourceRequirement(ResourceKind.Station, stationSystemId)
            ],
            operation.Definition.ResourceRequirements);
    }

    private static ProjectReleaseProductionRunLauncher CreateLauncher(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectReleaseArtifactStore releaseStore,
        IProjectRuntimeConfigurationSnapshotResolver configurationResolver,
        IProductionRunCoordinator coordinator)
    {
        var serializer = new FlowIrCanonicalSerializer();
        return new ProjectReleaseProductionRunLauncher(
            new ProjectReleaseSnapshotReader(scopeResolver, releaseStore),
            configurationResolver,
            coordinator,
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
            "line.main",
            PublishedAtUtc,
            [new SnapshotCapabilityBindingDetails(
                "motion.axis.move",
                "binding.axis.x",
                "Driver",
                "driver.axis.x",
                "station.main",
                "station.main")],
            [
                new ProjectTargetReferenceDetails("System", "station.main"),
                new ProjectTargetReferenceDetails("System", "station.secondary")
            ],
            [],
            releaseManifestPath,
            releaseContentSha256);
    }

    private static OpenedProjectReleaseArtifact OpenedRelease(
        int operationCount = 1,
        bool includeCondition = false)
    {
        var flowIr = FrozenFlowIr();
        var sourceRootPath = Path.Combine(
            LiveScope().ProjectPath,
            "releases",
            "release-snapshot.main",
            "source");
        var operations = new List<ProjectReleaseOperation>
        {
            Operation("operation.main", "station.main", "configuration.main", flowIr)
        };
        if (operationCount == 2)
        {
            operations.Add(Operation(
                "operation.secondary",
                "station.secondary",
                "configuration.secondary",
                flowIr));
        }

        ProjectReleaseRouteTransition[] transitions = includeCondition
            ?
            [
                new(
                    "transition.accepted",
                    "operation.main",
                    "operation.secondary",
                    null,
                    "Condition",
                    RequiredJudgement: null,
                    MaxTraversals: null,
                    ParallelGroupId: null,
                    OutputKey: "inspection.accepted",
                    ExpectedOutputKind: "Boolean",
                    ExpectedOutputValue: "true"),
                TerminalReleaseTransition("transition.main-fallback", "operation.main", "Held"),
                TerminalReleaseTransition("transition.secondary-completed", "operation.secondary", "Completed")
            ]
            : [TerminalReleaseTransition("transition.main-completed", "operation.main", "Completed")];

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
                    new ProjectReleaseProductModel("product.main", "MAINBOARD-A", "serialNumber"),
                    "operation.main",
                    operations,
                    transitions,
                    []),
                ExternalProgramResources: [],
                [new ProjectReleaseCapabilityBinding(
                    "motion.axis.move",
                    "binding.axis.x",
                    "Driver",
                    "driver.axis.x",
                    "station.main",
                    "station.main")],
                [
                    new ProjectReleaseTargetReference("System", "station.main"),
                    new ProjectReleaseTargetReference("System", "station.secondary")
                ],
                [],
                []),
            []);
    }

    private static ProjectReleaseRouteTransition TerminalReleaseTransition(
        string transitionId,
        string sourceOperationId,
        string disposition) => new(
            transitionId,
            sourceOperationId,
            null,
            disposition,
            "Sequence",
            null,
            null,
            null,
            null,
            null,
            null);

    private static ProjectReleaseOperation Operation(
        string operationId,
        string stationSystemId,
        string configurationSnapshotId,
        FlowIrCanonicalArtifact flowIr)
    {
        return new ProjectReleaseOperation(
            operationId,
            operationId,
            stationSystemId,
            "process.main",
            configurationSnapshotId,
            "process.main@1.0.0",
            flowIr.SchemaVersion,
            flowIr.Sha256,
            flowIr.CanonicalJson,
            [],
            [new ProjectReleaseOperationResource(
                $"resource.station.{operationId}",
                "Station",
                stationSystemId,
                "Fixed",
                [])],
            [],
            [new ProjectReleaseAuthorizedAction(
                "move:action:1",
                "move",
                "DeviceCommand",
                "motion.axis.move",
                "MoveAbsolute",
                "Capability",
                "motion.axis.move",
                5_000,
                null)]);
    }

    private static FlowIrCanonicalArtifact FrozenFlowIr()
    {
        var compilationResult = new ProcessFlowIrCompiler().Compile(PublishedProcessDefinition());
        Assert.True(compilationResult.IsSuccess, compilationResult.Error.Message);
        var artifactResult = new FlowIrCanonicalSerializer().Serialize(compilationResult.Value.Document);
        Assert.True(artifactResult.IsSuccess, artifactResult.Error.Message);
        return artifactResult.Value;
    }

    private static RuntimeConfigurationSnapshotDetails RuntimeConfiguration(
        string id,
        string stationSystemId)
    {
        return new RuntimeConfigurationSnapshotDetails(
            id,
            "process.main",
            "process.main@1.0.0",
            "recipe.main@1.0.0",
            stationSystemId);
    }

    private static ProjectApplicationWorkspaceScope LiveScope()
    {
        return new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            Path.Combine(Path.GetTempPath(), "openlineops-live-production-run-tests"),
            "applications/application.main/application.main.oloapp");
    }

    private static SubmitProjectReleaseProductionRunRequest SubmitRequest()
    {
        return new SubmitProjectReleaseProductionRunRequest(
            RunId,
            ProductionUnitId,
            "operator-1");
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
            cancellationToken.ThrowIfCancellationRequested();
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
            return ValueTask.FromException<ProjectReleaseArtifactDescriptor>(
                new InvalidOperationException("Publish is outside this test fixture."));
        }

        public ValueTask<OpenedProjectReleaseArtifact?> OpenAsync(
            ProjectApplicationWorkspaceScope scope,
            string snapshotId,
            string expectedContentSha256,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCallCount++;
            return ValueTask.FromResult(release);
        }

        public ValueTask RollbackPublicationAsync(
            ProjectApplicationWorkspaceScope scope,
            string snapshotId,
            string expectedContentSha256,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Rollback is outside this test fixture.");
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
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class RecordingProductionRunCoordinator : IProductionRunCoordinator
    {
        public SubmitProductionRunRequest? LastRequest { get; private set; }

        public ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
            SubmitProductionRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            var run = ProductionRun.Create(
                request.RunId,
                request.ProjectId,
                request.ApplicationId,
                request.ProjectSnapshotId,
                request.TopologyId,
                request.ProductionLineDefinitionId,
                request.ProductionUnitId,
                new ProductionUnitIdentity(
                    request.FrozenProductModelId,
                    request.FrozenIdentityInputKey,
                    "UNIT-1"),
                null,
                null,
                request.ActorId,
                request.EntryOperationId,
                PublishedAtUtc,
                request.Operations.Select(static operation => operation.Definition),
                request.RouteTransitions);
            return ValueTask.FromResult(Result.Success(run.ToSnapshot()));
        }

        public ValueTask<Result<ProductionRunSnapshot>> CommandAsync(
            ProductionRunId runId,
            ProductionRunCommandRequest command,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<Result<ProductionRunSnapshot>>(
                new InvalidOperationException("Commands are outside this test fixture."));
        }
    }
}
