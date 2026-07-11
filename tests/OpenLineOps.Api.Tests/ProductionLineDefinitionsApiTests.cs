using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Domain.Capabilities;
using OpenLineOps.Topology.Domain.DriverBindings;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Systems;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionLineDefinitionsApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _projectRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-production-api-tests",
        Guid.NewGuid().ToString("N"));

    public ProductionLineDefinitionsApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task PortableProductionLineCanBeCreatedAndReadThroughApi()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project.production.{suffix}";
        var applicationId = $"application.production.{suffix}";
        await SeedApplicationAsync(projectId, applicationId);
        var route = $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines";
        var request = new
        {
            lineDefinitionId = "line.main",
            displayName = "Main Production Line",
            topologyId = "topology.main",
            productModel = new
            {
                productModelId = "product.model-a",
                modelCode = "MODEL-A",
                identityInputKey = "serialNumber"
            },
            entryOperationId = "operation.load",
            operations = new object[]
            {
                new
                {
                    operationId = "operation.load",
                    displayName = "Load",
                    stationSystemId = "station.eol",
                    flowDefinitionId = "flow.load",
                    configurationSnapshotId = "configuration.load",
                    resources = StationResources("load")
                },
                new
                {
                    operationId = "operation.test",
                    displayName = "External Program",
                    stationSystemId = "station.eol",
                    flowDefinitionId = "flow.test",
                    configurationSnapshotId = "configuration.test",
                    resources = StationResources("test")
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "load-test",
                    sourceOperationId = "operation.load",
                    targetOperationId = "operation.test",
                    kind = "Sequence",
                    requiredJudgement = (string?)null,
                    maxTraversals = (int?)null,
                    parallelGroupId = (string?)null,
                    outputKey = (string?)null,
                    expectedOutputKind = (string?)null,
                    expectedOutputValue = (string?)null
                }
            },
            lineControllerAuthorizations = Array.Empty<object>()
        };

        using var createResponse = await _client.PostAsJsonAsync(route, request);
        var createBody = await createResponse.Content.ReadAsStringAsync();
        Assert.True(
            createResponse.StatusCode == HttpStatusCode.Created,
            $"Production Line create returned {(int)createResponse.StatusCode}: {createBody}");
        using var created = JsonDocument.Parse(createBody);
        Assert.Equal("line.main", created.RootElement.GetProperty("lineDefinitionId").GetString());
        Assert.Equal(
            "configuration.load",
            created.RootElement.GetProperty("operations")[0].GetProperty("configurationSnapshotId").GetString());
        Assert.Equal(
            "operation.test",
            created.RootElement.GetProperty("transitions")[0].GetProperty("targetOperationId").GetString());
        using var getResponse = await _client.GetAsync($"{route}/line.main");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var restored = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal("MODEL-A", restored.RootElement.GetProperty("productModel").GetProperty("modelCode").GetString());
        Assert.Equal(2, restored.RootElement.GetProperty("operations").GetArrayLength());
        Assert.Equal(
            "configuration.test",
            restored.RootElement.GetProperty("operations")[1].GetProperty("configurationSnapshotId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" configuration.load")]
    [InlineData("configuration.load ")]
    public async Task ApiRejectsMissingOrNonCanonicalOperationConfigurationSnapshotId(
        string? configurationSnapshotId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project.production.invalid-configuration.{suffix}";
        var applicationId = $"application.production.invalid-configuration.{suffix}";
        await SeedApplicationAsync(projectId, applicationId);
        var route = $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines";

        using var response = await _client.PostAsJsonAsync(route, new
        {
            lineDefinitionId = "line.invalid-configuration",
            displayName = "Invalid Configuration Production Line",
            topologyId = "topology.main",
            productModel = new
            {
                productModelId = "product.model-a",
                modelCode = "MODEL-A",
                identityInputKey = "serialNumber"
            },
            entryOperationId = "operation.load",
            operations = new[]
            {
                new
                {
                    operationId = "operation.load",
                    displayName = "Load",
                    stationSystemId = "station.eol",
                    flowDefinitionId = "flow.load",
                    configurationSnapshotId,
                    resources = StationResources("invalid")
                }
            },
            transitions = Array.Empty<object>(),
            lineControllerAuthorizations = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object[] StationResources(string suffix) =>
        [new
        {
            bindingId = $"resource.station.{suffix}",
            kind = "Station",
            topologyTargetId = "station.eol",
            resolution = "Fixed"
        }];

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_projectRoot))
        {
            Directory.Delete(_projectRoot, recursive: true);
        }
    }

    private async Task SeedApplicationAsync(string projectId, string applicationId)
    {
        using var serviceScope = _factory.Services.CreateScope();
        var workspaceService = serviceScope.ServiceProvider
            .GetRequiredService<IAutomationProjectWorkspaceService>();
        var created = await workspaceService.CreateAsync(new CreateAutomationProjectWorkspaceRequest(
            projectId,
            "Production API Project",
            _projectRoot,
            applicationId,
            "Production Application"));
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : string.Empty);
        var scope = await serviceScope.ServiceProvider
            .GetRequiredService<IProjectApplicationWorkspaceScopeResolver>()
            .ResolveAsync(projectId, applicationId);
        Assert.NotNull(scope);

        await serviceScope.ServiceProvider
            .GetRequiredService<IProjectAutomationTopologyRepository>()
            .SaveAsync(scope, CreateTopology());
        var processRepository = serviceScope.ServiceProvider
            .GetRequiredService<IProjectProcessDefinitionRepository>();
        await processRepository.SaveAsync(scope, CreatePublishedFlow(
            "flow.load",
            "native.inspect",
            "Inspect"));
        await processRepository.SaveAsync(scope, CreatePublishedFlow(
            "flow.test",
            "test.external",
            "ExecuteExternalProgram"));
    }

    private static AutomationTopology CreateTopology()
    {
        var topology = AutomationTopology.Create(
            new AutomationTopologyId("topology.main"),
            "Main",
            CreatedAtUtc);
        Assert.True(topology.AddCapability(CapabilityContract.Create(
            new CapabilityContractId("test.external"),
            "ExecuteExternalProgram",
            new Version(1, 0),
            "{}",
            "{}",
            TimeSpan.FromSeconds(30))).Succeeded);
        Assert.True(topology.AddCapability(CapabilityContract.Create(
            new CapabilityContractId("native.inspect"),
            "Inspect",
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
            providedCapabilities:
            [
                new CapabilityContractId("test.external"),
                new CapabilityContractId("native.inspect")
            ],
            metadata: new Dictionary<string, string>())).Succeeded);
        Assert.True(topology.AddDriverBinding(DriverBinding.Create(
            new DriverBindingId("binding.external"),
            new AutomationSystemId("station.eol"),
            new CapabilityContractId("test.external"),
            DriverProviderKind.ExternalSystem,
            "provider.test")).Succeeded);
        Assert.True(topology.AddDriverBinding(DriverBinding.Create(
            new DriverBindingId("binding.inspect"),
            new AutomationSystemId("station.eol"),
            new CapabilityContractId("native.inspect"),
            DriverProviderKind.Simulator,
            "simulator.inspect")).Succeeded);
        return topology;
    }

    private static ProcessDefinition CreatePublishedFlow(
        string id,
        string capability,
        string command)
    {
        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId(id),
            new ProcessVersionId($"{id}@1"),
            id,
            CreatedAtUtc);
        Assert.True(definition.AddNode(ProcessNode.Start(new ProcessNodeId("start"), "Start")).Succeeded);
        var action = ProcessNode.Command(
            new ProcessNodeId("action"),
            "Action",
            new ProcessCapabilityId(capability),
            ProcessActionTargetKind.Capability,
            capability,
            command,
            TimeSpan.FromSeconds(30),
            "{}");
        Assert.True(definition.AddNode(action).Succeeded);
        Assert.True(definition.AddNode(ProcessNode.End(new ProcessNodeId("end"), "End")).Succeeded);
        Assert.True(definition.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId("start-action"),
            new ProcessNodeId("start"),
            new ProcessNodeId("action"))).Succeeded);
        Assert.True(definition.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId("action-end"),
            new ProcessNodeId("action"),
            new ProcessNodeId("end"))).Succeeded);
        Assert.True(definition.Publish(CreatedAtUtc).Succeeded);
        return definition;
    }

}
