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
    public async Task CreateValidatesTopologyPublishedFlowsRouteAndExternalActionResource()
    {
        var fixture = await CreateFixtureAsync(externalActionMatches: true);

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
        var transition = Assert.Single(result.Value.Transitions);
        Assert.Equal("Sequence", transition.Kind);
        Assert.Equal("operation.test", transition.TargetOperationId);
        Assert.Equal("Provider", Assert.Single(result.Value.ExternalTestProgramAdapters).LaunchKind);
        Assert.True(File.Exists(Path.Combine(
            fixture.Scope.ApplicationRootPath,
            "production",
            "lines",
            "line.main",
            "line.json")));
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
        var fixture = await CreateFixtureAsync(externalActionMatches: true);
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
    public async Task CreateRejectsFlowReferenceToUnknownExternalResource()
    {
        var fixture = await CreateFixtureAsync(externalActionMatches: false);

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Production.ExternalTestActionResourceNotFound", result.Error.Code);
    }

    [Fact]
    public async Task CreateRejectsExternalActionThatBypassesOperationStationTarget()
    {
        var fixture = await CreateFixtureAsync(
            externalActionMatches: true,
            externalActionUsesBlockly: false);

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Production.OperationFlowTargetOutsideStation", result.Error.Code);
    }

    [Fact]
    public async Task CreateRejectsOrdinaryFlowActionTargetingAnotherStation()
    {
        var fixture = await CreateFixtureAsync(
            externalActionMatches: true,
            loadTargetId: "station.other");

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Production.OperationFlowTargetOutsideStation", result.Error.Code);
    }

    [Fact]
    public async Task CreateRejectsDeclaredExternalResourceUnusedByEveryFlow()
    {
        var fixture = await CreateFixtureAsync(
            externalActionMatches: true,
            flowContainsExternalAction: false);

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Production.ExternalTestActionMissing", result.Error.Code);
    }

    [Fact]
    public async Task CreateAcceptsPortableExecutableDeclarationWithoutExecutingIt()
    {
        var fixture = await CreateFixtureAsync(
            externalActionMatches: true,
            executableAdapter: true);

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request(useExecutable: true));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        var adapter = Assert.Single(result.Value.ExternalTestProgramAdapters);
        Assert.Equal("ApplicationExecutable", adapter.LaunchKind);
        Assert.Equal("programs/eol/test.exe", adapter.Executable);
        Assert.Null(adapter.ProviderKey);
    }

    [Fact]
    public async Task RepositoryRejectsFormerStageResourceShape()
    {
        var fixture = await CreateFixtureAsync(externalActionMatches: true);
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

    private async Task<ServiceFixture> CreateFixtureAsync(
        bool externalActionMatches,
        bool externalActionUsesBlockly = true,
        bool executableAdapter = false,
        bool flowContainsExternalAction = true,
        string loadTargetId = "station.eol")
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            _root,
            "applications/application.main/application.main.oloapp");
        if (executableAdapter)
        {
            var executablePath = Path.Combine(
                scope.ApplicationRootPath,
                "programs",
                "eol",
                "test.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
            await File.WriteAllBytesAsync(executablePath, [0x4d, 0x5a]);
        }

        var topologyRepository = new FileSystemProjectAutomationTopologyRepository();
        await topologyRepository.SaveAsync(
            scope,
            Topology(executableAdapter ? "adapter.test" : "provider.test"));
        var processRepository = new FileSystemProjectProcessDefinitionRepository();
        await processRepository.SaveAsync(scope, PublishedFlow(
            "flow.load",
            "native.inspect",
            "Inspect",
            adapterId: null,
            useBlockly: false,
            directSystemTargetId: loadTargetId));
        await processRepository.SaveAsync(scope, PublishedFlow(
            "flow.test",
            "test.external",
            "ExecuteTestProgram",
            flowContainsExternalAction
                ? externalActionMatches ? "adapter.test" : "wrong.adapter"
                : null,
            externalActionUsesBlockly));
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

    internal static SaveProductionLineDefinitionRequest Request(bool useExecutable = false)
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
                    "configuration.load"),
                new OperationDefinitionRequest(
                    "operation.test",
                    "External Test",
                    "station.eol",
                    "flow.test",
                    "configuration.test")
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
            [new ExternalTestProgramAdapterRequest(
                "adapter.test",
                "External EOL",
                "test.external",
                "ExecuteTestProgram",
                useExecutable ? "programs/eol/test.exe" : null,
                useExecutable ? null : "provider.test",
                ["--serial", "{{product.identity}}"],
                [
                    new ExternalTestProgramInputMappingRequest("$product.identity", "serial"),
                    new ExternalTestProgramInputMappingRequest("$product.model", "model")
                ],
                [new ExternalTestProgramResultMappingRequest("$.outcome", "test.outcome")],
                new ExternalTestProgramOutcomeMappingRequest(
                    "$.outcome",
                    "Passed",
                    "Failed",
                    "Aborted"),
                30_000)]);
    }

    internal static AutomationTopology Topology(string providerKey = "provider.test")
    {
        var topology = AutomationTopology.Create(
            new AutomationTopologyId("topology.main"),
            "Main",
            Now);
        Assert.True(topology.AddCapability(CapabilityContract.Create(
            new CapabilityContractId("test.external"),
            "ExecuteTestProgram",
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
            new CapabilityContractId("test.external"),
            DriverProviderKind.ExternalSystem,
            providerKey)).Succeeded);
        return topology;
    }

    internal static ProcessDefinition PublishedFlow(
        string flowId,
        string capabilityId,
        string commandName,
        string? adapterId,
        bool useBlockly,
        string directSystemTargetId = "station.eol")
    {
        var flow = ProcessDefinition.Create(
            new ProcessDefinitionId(flowId),
            new ProcessVersionId($"{flowId}@1"),
            flowId,
            Now);
        Assert.True(flow.AddNode(ProcessNode.Start(new ProcessNodeId("start"), "Start")).Succeeded);
        var action = adapterId is null || !useBlockly
            ? ProcessNode.Command(
                new ProcessNodeId("action"),
                "Action",
                new ProcessCapabilityId(capabilityId),
                adapterId is null
                    ? ProcessActionTargetKind.System
                    : ProcessActionTargetKind.Capability,
                adapterId is null ? directSystemTargetId : capabilityId,
                commandName,
                TimeSpan.FromSeconds(30),
                adapterId is null
                    ? "{}"
                    : $$"""{"externalTestProgramAdapterId":"{{adapterId}}"}""")
            : ProcessNode.Blockly(
                new ProcessNodeId("action"),
                "External Test Action",
                ExternalTestWorkspace(capabilityId, commandName, adapterId),
                TimeSpan.FromSeconds(30));
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

    private static string ExternalTestWorkspace(
        string capabilityId,
        string commandName,
        string adapterId) =>
        $$"""
        {
          "blocks": {
            "languageVersion": 0,
            "blocks": [
              {
                "type": "openlineops_run_external_test",
                "id": "external-test",
                "fields": {
                  "TARGET_KIND": "System",
                  "TARGET_ID": "station.eol",
                  "CAPABILITY": "{{capabilityId}}",
                  "COMMAND": "{{commandName}}",
                  "ADAPTER_ID": "{{adapterId}}",
                  "TIMEOUT_MS": 30000
                }
              }
            ]
          }
        }
        """;

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
