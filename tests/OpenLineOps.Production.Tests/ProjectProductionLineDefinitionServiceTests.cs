using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.ProjectWorkspaces;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Infrastructure.Persistence;
using OpenLineOps.Production.Application.LineDefinitions;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Production.Infrastructure.Persistence;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Slots;
using OpenLineOps.Topology.Domain.Systems;
using OpenLineOps.Topology.Domain.Topology;
using OpenLineOps.Topology.Infrastructure.Persistence;

namespace OpenLineOps.Production.Tests;

public sealed class ProjectProductionLineDefinitionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "openlineops-production-service-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateValidatesTopologyPublishedFlowsAndRoute()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal("product.model-a", result.Value.ProductModel.ProductModelId);
        Assert.Equal("operation.load", result.Value.EntryOperationId);
        Assert.Equal(
            ["operation.load", "operation.test"],
            result.Value.Operations.Select(operation => operation.OperationId));
        Assert.Equal(
            ["configuration.load", "configuration.test"],
            result.Value.Operations.Select(operation => operation.ConfigurationSnapshotId));
        Assert.All(result.Value.Operations, operation =>
        {
            var station = Assert.Single(operation.Resources);
            Assert.Equal("Station", station.Kind);
            Assert.Equal("Fixed", station.Resolution);
            Assert.Equal(operation.StationSystemId, station.TopologyTargetId);
        });
        var transition = Assert.Single(result.Value.Transitions);
        Assert.Equal("Sequence", transition.Kind);
        Assert.Equal("operation.test", transition.TargetOperationId);
        Assert.True(File.Exists(Path.Combine(
            fixture.Scope.ApplicationRootPath,
            "production",
            "lines",
            "line.main",
            "line.json")));
        var restored = await fixture.Service.GetByIdAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            "line.main");
        Assert.True(restored.IsSuccess, restored.IsFailure ? restored.Error.Message : string.Empty);
        Assert.Equal(
            result.Value.Operations.SelectMany(operation => operation.Resources),
            restored.Value.Operations.SelectMany(operation => operation.Resources));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" configuration.load")]
    [InlineData("configuration.load ")]
    public async Task ServiceRejectsMissingOrNonCanonicalOperationConfigurationSnapshotId(
        string? configurationSnapshotId)
    {
        var fixture = await CreateFixtureAsync();
        var request = Request() with
        {
            Operations = Request().Operations
                .Select(operation => operation.OperationId == "operation.load"
                    ? operation with { ConfigurationSnapshotId = configurationSnapshotId! }
                    : operation)
                .ToArray()
        };

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            request);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Production.InvalidLineDefinition", result.Error.Code);
    }

    [Fact]
    public async Task CreateRejectsOrdinaryFlowActionTargetingAnotherStation()
    {
        var fixture = await CreateFixtureAsync(loadTargetId: "station.other");

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Production.OperationFlowTargetOutsideStation", result.Error.Code);
    }

    [Theory]
    [InlineData("slot.other", "OperationFixedSlotResourceInvalid")]
    [InlineData("slot.disabled", "OperationFixedSlotResourceInvalid")]
    public async Task CreateRejectsCrossStationOrDisabledFixedSlot(
        string slotId,
        string expectedCode)
    {
        var fixture = await CreateFixtureAsync();
        var request = Request() with
        {
            Operations = Request().Operations.Select(operation =>
                operation.OperationId == "operation.load"
                    ? operation with
                    {
                        Resources =
                        [
                            .. operation.Resources,
                            new OperationResourceBindingRequest(
                                "resource.slot",
                                OperationResourceKind.Slot,
                                slotId,
                                OperationResourceResolution.Fixed)
                        ]
                    }
                    : operation).ToArray()
        };

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            request);

        Assert.True(result.IsFailure);
        Assert.Equal($"Validation.Production.{expectedCode}", result.Error.Code);
    }

    [Fact]
    public async Task RepositoryRejectsFormerStageResourceShape()
    {
        var fixture = await CreateFixtureAsync();
        var createResult = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());
        Assert.True(createResult.IsSuccess, createResult.IsFailure ? createResult.Error.Message : string.Empty);

        var path = Path.Combine(
            fixture.Scope.ApplicationRootPath,
            "production",
            "lines",
            "line.main",
            "line.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["stages"] = new JsonArray();
        await File.WriteAllTextAsync(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(
                fixture.Scope,
                new ProductionLineDefinitionId("line.main")));
        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<ServiceFixture> CreateFixtureAsync(string loadTargetId = "station.eol")
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            _root,
            "applications/application.main/application.main.oloapp");
        var topologyRepository = new FileSystemProjectAutomationTopologyRepository();
        await topologyRepository.SaveAsync(
            scope,
            Topology());
        var processRepository = new FileSystemProjectProcessDefinitionRepository();
        await processRepository.SaveAsync(scope, PublishedFlow(
            "flow.load",
            "native.inspect",
            "Inspect",
            directSystemTargetId: loadTargetId));
        await processRepository.SaveAsync(scope, PublishedFlow(
            "flow.test",
            "test.external",
            "ExecuteProgram"));
        var service = new ProjectProductionLineDefinitionService(
            new FixedScopeResolver(scope),
            new FileSystemProjectProductionLineDefinitionRepository(),
            topologyRepository,
            processRepository,
            new ProjectProcessBlocklyBlockCatalog(
                new FixedScopeResolver(scope),
                new FileSystemProjectProcessBlocklyBlockDefinitionRepository(),
                new FixedClock(Now)),
            new ProcessFlowIrCompiler(),
            new FixedClock(Now));
        return new ServiceFixture(scope, service);
    }

    internal static SaveProductionLineDefinitionRequest Request()
    {
        return new SaveProductionLineDefinitionRequest(
            "line.main",
            "Main Line",
            "topology.main",
            new ProductModelRequest("product.model-a", "MODEL-A", "serialNumber"),
            "operation.load",
            [
                new OperationDefinitionRequest(
                    "operation.load",
                    "Load",
                    "station.eol",
                    "flow.load",
                    "configuration.load",
                    StationResources("load")),
                new OperationDefinitionRequest(
                    "operation.test",
                    "External Program",
                    "station.eol",
                    "flow.test",
                    "configuration.test",
                    StationResources("test"))
            ],
            [new RouteTransitionRequest(
                "load-test",
                "operation.load",
                "operation.test",
                RouteTransitionKind.Sequence,
                null,
                null,
                null,
                null,
                null,
                null)],
            []);
    }

    private static OperationResourceBindingRequest[] StationResources(string suffix) =>
        [new OperationResourceBindingRequest(
            $"resource.station.{suffix}",
            OperationResourceKind.Station,
            "station.eol",
            OperationResourceResolution.Fixed)];

    internal static AutomationTopology Topology(string providerKey = "provider.test")
    {
        var topology = AutomationTopology.Create(
            new AutomationTopologyId("topology.main"),
            "Main",
            Now);
        Assert.True(topology.AddCapability(CapabilityContract.Create(
            new CapabilityContractId("test.external"),
            "ExecuteProgram",
            new Version(1, 0),
            "{}",
            "{}",
            TimeSpan.FromSeconds(30))).Succeeded);
        Assert.True(topology.AddSystem(AutomationSystem.Create(
            new AutomationSystemId("station.eol"),
            null,
            SystemKind.Station,
            "TestSystem",
            "Tester",
            providedCapabilities: [new CapabilityContractId("test.external")],
            metadata: new Dictionary<string, string>())).Succeeded);
        Assert.True(topology.AddSystem(AutomationSystem.Create(
            new AutomationSystemId("station.other"),
            null,
            SystemKind.Station,
            "OtherSystem",
            "Other Station",
            metadata: new Dictionary<string, string>())).Succeeded);
        Assert.True(topology.AddDriverBinding(DriverBinding.Create(
            new DriverBindingId("binding.external"),
            new AutomationSystemId("station.eol"),
            new CapabilityContractId("test.external"),
            DriverProviderKind.ExternalSystem,
            providerKey)).Succeeded);
        Assert.True(topology.AddSlotGroup(SlotGroup.Create(
            new SlotGroupId("group.eol"),
            new AutomationSystemId("station.eol"),
            "EOL Slots",
            SlotGroupKind.TesterBank,
            2)).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.enabled"),
            new SlotGroupId("group.eol"),
            new AutomationSystemId("station.eol"),
            "01",
            "Enabled",
            isEnabled: true)).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.disabled"),
            new SlotGroupId("group.eol"),
            new AutomationSystemId("station.eol"),
            "02",
            "Disabled",
            isEnabled: false)).Succeeded);
        Assert.True(topology.AddSlotGroup(SlotGroup.Create(
            new SlotGroupId("group.other"),
            new AutomationSystemId("station.other"),
            "Other Slots",
            SlotGroupKind.TesterBank,
            1)).Succeeded);
        Assert.True(topology.AddSlot(SlotDefinition.Create(
            new SlotDefinitionId("slot.other"),
            new SlotGroupId("group.other"),
            new AutomationSystemId("station.other"),
            "01",
            "Other",
            isEnabled: true)).Succeeded);
        return topology;
    }

    internal static ProcessDefinition PublishedFlow(
        string flowId,
        string capabilityId,
        string commandName,
        string directSystemTargetId = "station.eol")
    {
        var flow = ProcessDefinition.Create(
            new ProcessDefinitionId(flowId),
            new ProcessVersionId($"{flowId}@1"),
            flowId,
            Now);
        Assert.True(flow.AddNode(ProcessNode.Start(new ProcessNodeId("start"), "Start")).Succeeded);
        var action = ProcessNode.Command(
                new ProcessNodeId("action"),
                "Action",
                new ProcessCapabilityId(capabilityId),
                ProcessActionTargetKind.System,
                directSystemTargetId,
                commandName,
                TimeSpan.FromSeconds(30),
                "{}");
        Assert.True(flow.AddNode(action).Succeeded);
        Assert.True(flow.AddNode(ProcessNode.End(new ProcessNodeId("end"), "End")).Succeeded);
        Assert.True(flow.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId("start-action"),
            new ProcessNodeId("start"),
            new ProcessNodeId("action"))).Succeeded);
        Assert.True(flow.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId("action-end"),
            new ProcessNodeId("action"),
            new ProcessNodeId("end"))).Succeeded);
        Assert.True(flow.Publish(Now).Succeeded);
        return flow;
    }

    private sealed class FixedScopeResolver(ProjectApplicationWorkspaceScope scope)
        : IProjectApplicationWorkspaceScopeResolver
    {
        public ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
            string projectId,
            string applicationId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<ProjectApplicationWorkspaceScope?>(
                projectId == scope.ProjectId && applicationId == scope.ApplicationId ? scope : null);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed record ServiceFixture(
        ProjectApplicationWorkspaceScope Scope,
        ProjectProductionLineDefinitionService Service);
}
