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
            dutModel = new
            {
                dutModelId = "dut.model-a",
                modelCode = "MODEL-A",
                identityInputKey = "serialNumber"
            },
            workstations = new[]
            {
                new
                {
                    workstationId = "workstation.eol",
                    displayName = "EOL",
                    stationSystemId = "station.eol"
                }
            },
            stages = new object[]
            {
                new
                {
                    stageId = "stage.load",
                    sequence = 1,
                    displayName = "Load",
                    workstationId = "workstation.eol",
                    flowDefinitionId = "flow.load",
                    configurationSnapshotId = "configuration.load",
                    externalTestProgramAdapterId = (string?)null
                },
                new
                {
                    stageId = "stage.test",
                    sequence = 2,
                    displayName = "External Test",
                    workstationId = "workstation.eol",
                    flowDefinitionId = "flow.test",
                    configurationSnapshotId = "configuration.test",
                    externalTestProgramAdapterId = "adapter.test"
                }
            },
            externalTestProgramAdapters = new[]
            {
                new
                {
                    adapterId = "adapter.test",
                    displayName = "External EOL",
                    capabilityId = "test.external",
                    commandName = "ExecuteTestProgram",
                    executable = (string?)null,
                    providerKey = "provider.test",
                    argumentTemplates = new[] { "--serial", "{{dut.identity}}" },
                    inputMappings = new[]
                    {
                        new { source = "$dut.identity", target = "serial" },
                        new { source = "$dut.model", target = "model" }
                    },
                    resultMappings = new[]
                    {
                        new { sourcePath = "$.outcome", targetKey = "test.outcome" }
                    },
                    outcomeMapping = new
                    {
                        sourcePath = "$.outcome",
                        passedToken = "Passed",
                        failedToken = "Failed",
                        abortedToken = "Aborted"
                    },
                    timeoutMilliseconds = 30_000L
                }
            }
        };

        using var createResponse = await _client.PostAsJsonAsync(route, request);
        var createBody = await createResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var created = JsonDocument.Parse(createBody);
        Assert.Equal("line.main", created.RootElement.GetProperty("lineDefinitionId").GetString());
        Assert.Equal(
            "configuration.load",
            created.RootElement.GetProperty("stages")[0].GetProperty("configurationSnapshotId").GetString());
        Assert.Equal("stage.test", created.RootElement.GetProperty("stages")[0].GetProperty("nextStageId").GetString());
        Assert.Equal("Provider", created.RootElement
            .GetProperty("externalTestProgramAdapters")[0]
            .GetProperty("launchKind")
            .GetString());
        Assert.Equal("Passed", created.RootElement
            .GetProperty("externalTestProgramAdapters")[0]
            .GetProperty("outcomeMapping")
            .GetProperty("passedToken")
            .GetString());

        using var getResponse = await _client.GetAsync($"{route}/line.main");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var restored = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal("MODEL-A", restored.RootElement.GetProperty("dutModel").GetProperty("modelCode").GetString());
        Assert.Equal(2, restored.RootElement.GetProperty("stages").GetArrayLength());
        Assert.Equal(
            "configuration.test",
            restored.RootElement.GetProperty("stages")[1].GetProperty("configurationSnapshotId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" configuration.load")]
    [InlineData("configuration.load ")]
    public async Task ApiRejectsMissingOrNonCanonicalStageConfigurationSnapshotId(
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
            dutModel = new
            {
                dutModelId = "dut.model-a",
                modelCode = "MODEL-A",
                identityInputKey = "serialNumber"
            },
            workstations = new[]
            {
                new
                {
                    workstationId = "workstation.eol",
                    displayName = "EOL",
                    stationSystemId = "station.eol"
                }
            },
            stages = new[]
            {
                new
                {
                    stageId = "stage.load",
                    sequence = 1,
                    displayName = "Load",
                    workstationId = "workstation.eol",
                    flowDefinitionId = "flow.load",
                    configurationSnapshotId,
                    externalTestProgramAdapterId = (string?)null
                }
            },
            externalTestProgramAdapters = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

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
            "Inspect",
            adapterId: null));
        await processRepository.SaveAsync(scope, CreatePublishedFlow(
            "flow.test",
            "test.external",
            "ExecuteTestProgram",
            "adapter.test"));
    }

    private static AutomationTopology CreateTopology()
    {
        var topology = AutomationTopology.Create(
            new AutomationTopologyId("topology.main"),
            "Main",
            CreatedAtUtc);
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
            "provider.test")).Succeeded);
        return topology;
    }

    private static ProcessDefinition CreatePublishedFlow(
        string id,
        string capability,
        string command,
        string? adapterId)
    {
        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId(id),
            new ProcessVersionId($"{id}@1"),
            id,
            CreatedAtUtc);
        Assert.True(definition.AddNode(ProcessNode.Start(new ProcessNodeId("start"), "Start")).Succeeded);
        var action = adapterId is null
            ? ProcessNode.Command(
                new ProcessNodeId("action"),
                "Action",
                new ProcessCapabilityId(capability),
                ProcessActionTargetKind.Capability,
                capability,
                command,
                TimeSpan.FromSeconds(30),
                "{}")
            : ProcessNode.Blockly(
                new ProcessNodeId("action"),
                "External Test Action",
                ExternalTestWorkspace(capability, command, adapterId),
                TimeSpan.FromSeconds(30));
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

    private static string ExternalTestWorkspace(
        string capability,
        string command,
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
                  "CAPABILITY": "{{capability}}",
                  "COMMAND": "{{command}}",
                  "ADAPTER_ID": "{{adapterId}}",
                  "TIMEOUT_MS": 30000
                }
              }
            ]
          }
        }
        """;
}
