using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class ProductionLineDefinitionsApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>, IDisposable
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly OpenLineOpsApiWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _projectRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-production-api-tests",
        Guid.NewGuid().ToString("N"));

    public ProductionLineDefinitionsApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(new WebApplicationFactoryClientOptions
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
                    inputMappings = Array.Empty<object>(),
                    resources = StationResources("load")
                },
                new
                {
                    operationId = "operation.test",
                    displayName = "External Program",
                    stationSystemId = "station.eol",
                    flowDefinitionId = "flow.test",
                    configurationSnapshotId = "configuration.test",
                    inputMappings = Array.Empty<object>(),
                    resources = StationResources("test")
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "load-test",
                    sourceOperationId = "operation.load",
                    targetOperationId = (string?)"operation.test",
                    terminalDisposition = (string?)null,
                    kind = "Sequence",
                    requiredJudgement = (string?)null,
                    maxTraversals = (int?)null,
                    parallelGroupId = (string?)null,
                    outputKey = (string?)null,
                    expectedOutputKind = (string?)null,
                    expectedOutputValue = (string?)null
                },
                new
                {
                    transitionId = "test-completed",
                    sourceOperationId = "operation.test",
                    targetOperationId = (string?)null,
                    terminalDisposition = (string?)"Completed",
                    kind = "Sequence",
                    requiredJudgement = (string?)null,
                    maxTraversals = (int?)null,
                    parallelGroupId = (string?)null,
                    outputKey = (string?)null,
                    expectedOutputKind = (string?)null,
                    expectedOutputValue = (string?)null
                }
            },
            lineControllerAuthorizations = Array.Empty<object>(),
            routeLayout = new
            {
                operationPositions = new[]
                {
                    new { operationId = "operation.load", x = 120, y = 80 },
                    new { operationId = "operation.test", x = 400, y = 80 }
                }
            }
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
        Assert.Equal(
            400,
            created.RootElement.GetProperty("routeLayout").GetProperty("operationPositions")[1]
                .GetProperty("x").GetInt32());
        using var getResponse = await _client.GetAsync($"{route}/line.main");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var restored = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal("MODEL-A", restored.RootElement.GetProperty("productModel").GetProperty("modelCode").GetString());
        Assert.Equal(2, restored.RootElement.GetProperty("operations").GetArrayLength());
        Assert.Equal(
            "configuration.test",
            restored.RootElement.GetProperty("operations")[1].GetProperty("configurationSnapshotId").GetString());
    }

    [Fact]
    public async Task RouteLayoutUsesTheSameRevisionAndRejectsStaleWrites()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project.production.layout-revision.{suffix}";
        var applicationId = $"application.production.layout-revision.{suffix}";
        await SeedApplicationAsync(projectId, applicationId);
        var route = $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines";
        var request = ValidLineRequestNode("line.layout-revision");

        using var createResponse = await _client.PostAsync(route, JsonContent(request));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync())!.AsObject();
        var originalRevision = created["revision"]!.GetValue<string>();
        request["routeLayout"]!["operationPositions"]![0]!["x"] = 720;

        using var replaceRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"{route}/line.layout-revision")
        {
            Content = JsonContent(request)
        };
        replaceRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{originalRevision}\"");
        using var replaceResponse = await _client.SendAsync(replaceRequest);
        Assert.Equal(HttpStatusCode.OK, replaceResponse.StatusCode);
        var replaced = JsonNode.Parse(await replaceResponse.Content.ReadAsStringAsync())!.AsObject();
        var nextRevision = replaced["revision"]!.GetValue<string>();
        Assert.NotEqual(originalRevision, nextRevision);
        Assert.Equal(720, replaced["routeLayout"]!["operationPositions"]![0]!["x"]!.GetValue<int>());

        request["routeLayout"]!["operationPositions"]![0]!["x"] = 900;
        using var staleRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"{route}/line.layout-revision")
        {
            Content = JsonContent(request)
        };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{originalRevision}\"");
        using var staleResponse = await _client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("extra")]
    [InlineData("case-ambiguous")]
    [InlineData("out-of-bounds")]
    public async Task ApiRejectsStrictlyInvalidRouteLayouts(string mutation)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project.production.invalid-layout.{suffix}";
        var applicationId = $"application.production.invalid-layout.{suffix}";
        await SeedApplicationAsync(projectId, applicationId);
        var route = $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines";
        var request = ValidLineRequestNode($"line.invalid-layout-{mutation}");
        var positions = request["routeLayout"]!["operationPositions"]!.AsArray();
        switch (mutation)
        {
            case "missing":
                request.Remove("routeLayout");
                break;
            case "empty":
                positions.Clear();
                break;
            case "extra":
                positions.Add(new JsonObject
                {
                    ["operationId"] = "operation.extra",
                    ["x"] = 680,
                    ["y"] = 80
                });
                break;
            case "case-ambiguous":
                positions[1]!["operationId"] = "OPERATION.LOAD";
                break;
            case "out-of-bounds":
                positions[0]!["x"] = 100_001;
                break;
        }

        using var response = await _client.PostAsync(route, JsonContent(request));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
                    inputMappings = Array.Empty<object>(),
                    resources = StationResources("invalid")
                }
            },
            transitions = Array.Empty<object>(),
            lineControllerAuthorizations = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("FixedPoint", "3.50")]
    [InlineData("DateTimeUtc", "2026-07-15T12:34:56.1234567Z")]
    public async Task ApiRejectsNonUniqueProductionContextConditionValues(
        string expectedOutputKind,
        string expectedOutputValue)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project.production.invalid-context-value.{suffix}";
        var applicationId = $"application.production.invalid-context-value.{suffix}";
        await SeedApplicationAsync(projectId, applicationId);
        var route = $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines";
        var request = ConditionalLineRequestNode(
            "line.invalid-context-value",
            expectedOutputKind,
            expectedOutputValue);

        using var response = await _client.PostAsync(route, JsonContent(request));
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not canonical", responseBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FixedPoint", "3.5")]
    [InlineData("DateTimeUtc", "2026-07-15T12:34:56.1234567+00:00")]
    public async Task ApiAcceptsUniqueCanonicalProductionContextConditionValues(
        string expectedOutputKind,
        string expectedOutputValue)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project.production.canonical-context-value.{suffix}";
        var applicationId = $"application.production.canonical-context-value.{suffix}";
        await SeedApplicationAsync(projectId, applicationId);
        var route = $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines";
        var request = ConditionalLineRequestNode(
            "line.canonical-context-value",
            expectedOutputKind,
            expectedOutputValue);

        using var response = await _client.PostAsync(route, JsonContent(request));
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Production Line create returned {(int)response.StatusCode}: {responseBody}");
        using var created = JsonDocument.Parse(responseBody);
        var condition = created.RootElement.GetProperty("transitions")
            .EnumerateArray()
            .Single(transition => transition.GetProperty("kind").GetString() == "Condition");
        Assert.Equal(expectedOutputKind, condition.GetProperty("expectedOutputKind").GetString());
        Assert.Equal(expectedOutputValue, condition.GetProperty("expectedOutputValue").GetString());
    }

    private static object[] StationResources(string suffix) =>
        [new
        {
            bindingId = $"resource.station.{suffix}",
            kind = "Station",
            topologyTargetId = "station.eol",
            resolution = "Fixed"
        }];

    private static JsonObject ValidLineRequestNode(string lineDefinitionId) => new()
    {
        ["lineDefinitionId"] = lineDefinitionId,
        ["displayName"] = "Layout Production Line",
        ["topologyId"] = "topology.main",
        ["productModel"] = new JsonObject
        {
            ["productModelId"] = "product.model-a",
            ["modelCode"] = "MODEL-A",
            ["identityInputKey"] = "serialNumber"
        },
        ["entryOperationId"] = "operation.load",
        ["operations"] = new JsonArray
        {
            OperationNode("operation.load", "Load", "flow.load", "configuration.load", "load"),
            OperationNode("operation.test", "Test", "flow.test", "configuration.test", "test")
        },
        ["transitions"] = new JsonArray
        {
            TransitionNode("load-test", "operation.load", "operation.test", null),
            TransitionNode("test-completed", "operation.test", null, "Completed")
        },
        ["lineControllerAuthorizations"] = new JsonArray(),
        ["routeLayout"] = new JsonObject
        {
            ["operationPositions"] = new JsonArray
            {
                new JsonObject { ["operationId"] = "operation.load", ["x"] = 120, ["y"] = 80 },
                new JsonObject { ["operationId"] = "operation.test", ["x"] = 400, ["y"] = 80 }
            }
        }
    };

    private static JsonObject OperationNode(
        string operationId,
        string displayName,
        string flowDefinitionId,
        string configurationSnapshotId,
        string resourceSuffix) => new()
        {
            ["operationId"] = operationId,
            ["displayName"] = displayName,
            ["stationSystemId"] = "station.eol",
            ["flowDefinitionId"] = flowDefinitionId,
            ["configurationSnapshotId"] = configurationSnapshotId,
            ["inputMappings"] = new JsonArray(),
            ["resources"] = new JsonArray
        {
            new JsonObject
            {
                ["bindingId"] = $"resource.station.{resourceSuffix}",
                ["kind"] = "Station",
                ["topologyTargetId"] = "station.eol",
                ["resolution"] = "Fixed"
            }
        }
        };

    private static JsonObject ConditionalLineRequestNode(
        string lineDefinitionId,
        string expectedOutputKind,
        string expectedOutputValue)
    {
        var request = ValidLineRequestNode(lineDefinitionId);
        var transitions = request["transitions"]!.AsArray();
        var condition = transitions[0]!.AsObject();
        condition["kind"] = "Condition";
        condition["outputKey"] = "inspection.value";
        condition["expectedOutputKind"] = expectedOutputKind;
        condition["expectedOutputValue"] = expectedOutputValue;
        transitions.Insert(
            1,
            TransitionNode(
                "load-test-fallback",
                "operation.load",
                "operation.test",
                null));
        return request;
    }

    private static JsonObject TransitionNode(
        string transitionId,
        string sourceOperationId,
        string? targetOperationId,
        string? terminalDisposition) => new()
        {
            ["transitionId"] = transitionId,
            ["sourceOperationId"] = sourceOperationId,
            ["targetOperationId"] = targetOperationId,
            ["terminalDisposition"] = terminalDisposition,
            ["kind"] = "Sequence",
            ["requiredJudgement"] = null,
            ["maxTraversals"] = null,
            ["parallelGroupId"] = null,
            ["outputKey"] = null,
            ["expectedOutputKind"] = null,
            ["expectedOutputValue"] = null
        };

    private static StringContent JsonContent(JsonObject content) => new(
        content.ToJsonString(),
        Encoding.UTF8,
        "application/json");

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
