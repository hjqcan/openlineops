using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Infrastructure.Persistence;
using OpenLineOps.Production.Application.LineDefinitions;
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
    public async Task CreateValidatesTopologyPublishedFlowsAndStandardExternalAction()
    {
        var fixture = await CreateFixtureAsync(externalActionMatches: true);

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(["stage.load", "stage.test"], result.Value.Stages.Select(stage => stage.StageId));
        Assert.Equal("stage.test", result.Value.Stages.First().NextStageId);
        Assert.Null(result.Value.Stages.Last().NextStageId);
        Assert.Equal("Provider", Assert.Single(result.Value.ExternalTestProgramAdapters).LaunchKind);
        Assert.True(File.Exists(Path.Combine(
            fixture.Scope.ApplicationRootPath,
            "production",
            "lines",
            "line.main",
            "line.json")));
    }

    [Fact]
    public async Task CreateRejectsExternalStageWithoutMatchingCommandAction()
    {
        var fixture = await CreateFixtureAsync(externalActionMatches: false);

        var result = await fixture.Service.CreateAsync(
            fixture.Scope.ProjectId,
            fixture.Scope.ApplicationId,
            Request());

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Production.ExternalTestActionMissing", result.Error.Code);
    }

    [Fact]
    public async Task CreateRejectsCapabilityTargetCommandThatBypassesWorkstationTargetLock()
    {
        var fixture = await CreateFixtureAsync(
            externalActionMatches: true,
            externalActionUsesBlockly: false);

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
        bool executableAdapter = false)
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
            useBlockly: false));
        await processRepository.SaveAsync(scope, PublishedFlow(
            "flow.test",
            "test.external",
            "ExecuteTestProgram",
            externalActionMatches ? "adapter.test" : "wrong.adapter",
            externalActionUsesBlockly));
        var service = new ProjectProductionLineDefinitionService(
            new FixedScopeResolver(scope),
            new FileSystemProjectProductionLineDefinitionRepository(),
            topologyRepository,
            processRepository,
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
            new DutModelRequest("dut.model-a", "MODEL-A", "serialNumber"),
            [new WorkstationRequest("workstation.eol", "EOL", "station.eol")],
            [
                new ProcessStageRequest("stage.load", 1, "Load", "workstation.eol", "flow.load", null),
                new ProcessStageRequest(
                    "stage.test",
                    2,
                    "External Test",
                    "workstation.eol",
                    "flow.test",
                    "adapter.test")
            ],
            [new ExternalTestProgramAdapterRequest(
                "adapter.test",
                "External EOL",
                "test.external",
                "ExecuteTestProgram",
                useExecutable ? "programs/eol/test.exe" : null,
                useExecutable ? null : "provider.test",
                ["--serial", "{{dut.identity}}"],
                [
                    new ExternalTestProgramInputMappingRequest("$dut.identity", "serial"),
                    new ExternalTestProgramInputMappingRequest("$dut.model", "model")
                ],
                [new ExternalTestProgramResultMappingRequest("$.outcome", "test.outcome")],
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
        bool useBlockly)
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
