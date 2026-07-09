using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class AutomationProjectWorkspaceApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] SnapshotBlockVersionIds = ["block.motion.axis.move@1.0.0"];

    private readonly HttpClient _client;

    public AutomationProjectWorkspaceApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task ProjectWorkspaceManifestCanBeCreatedSavedAndOpened()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-workspace-manifest-{suffix}";
        var applicationId = $"application-workspace-{suffix}";
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-api-project-workspace-tests",
            suffix);

        try
        {
            using var createWorkspaceResponse = await _client.PostAsJsonAsync(
                "/api/automation-project-workspaces",
                new
                {
                    projectId,
                    displayName = "Workspace Manifest Project",
                    projectPath = projectDirectory,
                    defaultApplicationId = applicationId,
                    defaultApplicationName = "Workspace Application"
                });
            using var createWorkspaceBody = await ReadJsonAsync(createWorkspaceResponse);

            Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);
            Assert.Equal(
                projectId,
                createWorkspaceBody.RootElement.GetProperty("project").GetProperty("projectId").GetString());
            Assert.True(File.Exists(createWorkspaceBody.RootElement.GetProperty("manifestPath").GetString()));

            using var saveManifestResponse = await _client.PutAsync(
                $"/api/automation-projects/{projectId}/manifest",
                content: null);
            using var saveManifestBody = await ReadJsonAsync(saveManifestResponse);

            Assert.Equal(HttpStatusCode.OK, saveManifestResponse.StatusCode);
            Assert.Equal(
                applicationId,
                saveManifestBody.RootElement
                    .GetProperty("manifest")
                    .GetProperty("applications")[0]
                    .GetProperty("applicationId")
                    .GetString());

            using var openWorkspaceResponse = await _client.PostAsJsonAsync(
                "/api/automation-project-workspaces/open",
                new
                {
                    projectPath = projectDirectory
                });
            using var openWorkspaceBody = await ReadJsonAsync(openWorkspaceResponse);

            Assert.Equal(HttpStatusCode.OK, openWorkspaceResponse.StatusCode);
            Assert.Equal(
                projectId,
                openWorkspaceBody.RootElement.GetProperty("project").GetProperty("projectId").GetString());
            Assert.Equal(
                applicationId,
                openWorkspaceBody.RootElement
                    .GetProperty("project")
                    .GetProperty("applications")[0]
                    .GetProperty("applicationId")
                    .GetString());
        }
        finally
        {
            if (Directory.Exists(projectDirectory))
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProjectWorkspaceCompositionCanBeCreatedAndPublished()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-workspace-{suffix}";
        var applicationId = $"application-inspection-{suffix}";
        var topologyId = $"topology-cell-{suffix}";
        var siteNodeId = $"site-main-{suffix}";
        var stationNodeId = $"station-left-{suffix}";
        var capabilityId = $"motion-axis-move-{suffix}";
        var moduleId = $"module-axis-{suffix}";
        var bindingId = $"binding-axis-{suffix}";
        var slotGroupId = $"slot-group-left-nest-{suffix}";
        var slotId = $"slot-left-nest-1-{suffix}";
        var layoutId = $"layout-topdown-{suffix}";
        var processDefinitionId = $"process-inspection-{suffix}";
        var snapshotId = $"snapshot-project-{suffix}";

        using var createTopologyResponse = await _client.PostAsJsonAsync(
            "/api/automation-topologies",
            new
            {
                topologyId,
                displayName = "Inspection Cell Topology"
            });
        using var createTopologyBody = await ReadJsonAsync(createTopologyResponse);

        Assert.Equal(HttpStatusCode.Created, createTopologyResponse.StatusCode);
        Assert.Equal(topologyId, createTopologyBody.RootElement.GetProperty("topologyId").GetString());

        await AddNodeAsync(topologyId, siteNodeId, parentNodeId: null, kind: "Site", displayName: "Main Site");
        await AddNodeAsync(topologyId, stationNodeId, siteNodeId, kind: "Station", displayName: "Left Station");

        using var capabilityResponse = await _client.PostAsJsonAsync(
            $"/api/automation-topologies/{topologyId}/capabilities",
            new
            {
                capabilityId,
                commandName = "MoveAxis",
                version = "1.0.0",
                inputSchema = """{"type":"object"}""",
                outputSchema = (string?)null,
                timeoutSeconds = 30,
                safetyClass = "Motion"
            });

        Assert.Equal(HttpStatusCode.OK, capabilityResponse.StatusCode);

        using var moduleResponse = await _client.PostAsJsonAsync(
            $"/api/automation-topologies/{topologyId}/modules",
            new
            {
                moduleId,
                nodeId = stationNodeId,
                moduleKind = "AxisMotion",
                displayName = "X Axis",
                requiredCapabilityIds = new[] { capabilityId },
                providedCapabilityIds = new[] { capabilityId }
            });

        Assert.Equal(HttpStatusCode.OK, moduleResponse.StatusCode);

        using var bindingResponse = await _client.PostAsJsonAsync(
            $"/api/automation-topologies/{topologyId}/driver-bindings",
            new
            {
                bindingId,
                capabilityId,
                providerKind = "Simulator",
                providerKey = "simulator://axis-x"
            });

        Assert.Equal(HttpStatusCode.OK, bindingResponse.StatusCode);

        using var slotGroupResponse = await _client.PostAsJsonAsync(
            $"/api/automation-topologies/{topologyId}/slot-groups",
            new
            {
                slotGroupId,
                parentNodeId = stationNodeId,
                displayName = "Left Nest",
                kind = "FixtureNest",
                capacity = 2
            });

        Assert.Equal(HttpStatusCode.OK, slotGroupResponse.StatusCode);

        using var slotResponse = await _client.PostAsJsonAsync(
            $"/api/automation-topologies/{topologyId}/slots",
            new
            {
                slotGroupId,
                slotId,
                parentNodeId = stationNodeId,
                address = "L1",
                displayName = "Left Nest 1",
                materialKind = "Dut",
                isEnabled = true
            });
        using var slotBody = await ReadJsonAsync(slotResponse);

        Assert.Equal(HttpStatusCode.OK, slotResponse.StatusCode);
        Assert.Contains(
            slotBody.RootElement.GetProperty("slots").EnumerateArray(),
            slot => slot.GetProperty("slotId").GetString() == slotId);

        using var createLayoutResponse = await _client.PostAsJsonAsync(
            "/api/site-layouts",
            new
            {
                layoutId,
                topologyId,
                displayName = "Main Top Down Layout",
                canvasWidth = 1200,
                canvasHeight = 800,
                units = "mm"
            });

        Assert.Equal(HttpStatusCode.Created, createLayoutResponse.StatusCode);

        using var addLayoutElementResponse = await _client.PostAsJsonAsync(
            $"/api/site-layouts/{layoutId}/elements",
            new
            {
                elementId = $"layout-slot-{suffix}",
                kind = "SlotShape",
                targetKind = "Slot",
                targetId = slotId,
                x = 100,
                y = 140,
                width = 80,
                height = 60,
                rotationDegrees = 0,
                layerId = "slots",
                label = "L1"
            });
        using var layoutBody = await ReadJsonAsync(addLayoutElementResponse);

        Assert.Equal(HttpStatusCode.OK, addLayoutElementResponse.StatusCode);
        Assert.Single(layoutBody.RootElement.GetProperty("elements").EnumerateArray());
        Assert.Equal(slotId, layoutBody.RootElement.GetProperty("elements")[0].GetProperty("targetId").GetString());

        using var createProjectResponse = await _client.PostAsJsonAsync(
            "/api/automation-projects",
            new
            {
                projectId,
                displayName = "Inspection Project",
                projectPath = $"C:/OpenLineOps/Projects/{projectId}"
            });

        Assert.Equal(HttpStatusCode.Created, createProjectResponse.StatusCode);

        using var addApplicationResponse = await _client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/applications",
            new
            {
                applicationId,
                displayName = "Inspection Application"
            });

        Assert.Equal(HttpStatusCode.OK, addApplicationResponse.StatusCode);

        using var linkTopologyResponse = await _client.PutAsJsonAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/topology",
            new
            {
                topologyId
            });

        Assert.Equal(HttpStatusCode.OK, linkTopologyResponse.StatusCode);

        using var linkProcessResponse = await _client.PutAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/process-definitions/{processDefinitionId}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, linkProcessResponse.StatusCode);

        using var publishSnapshotResponse = await _client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/snapshots",
            new
            {
                snapshotId,
                applicationId,
                topologyId,
                processDefinitionId,
                processVersionId = $"{processDefinitionId}@1.0.0",
                configurationSnapshotId = $"configuration-{suffix}",
                capabilityBindings = new[]
                {
                    new
                    {
                        capabilityId,
                        bindingId,
                        providerKind = "Simulator",
                        providerKey = "simulator://axis-x"
                    }
                },
                targetReferences = new[]
                {
                    new
                    {
                        kind = "slot",
                        targetId = slotId
                    }
                },
                blockVersionIds = SnapshotBlockVersionIds
            });
        using var projectBody = await ReadJsonAsync(publishSnapshotResponse);

        Assert.Equal(HttpStatusCode.Created, publishSnapshotResponse.StatusCode);
        Assert.Equal(snapshotId, projectBody.RootElement.GetProperty("activeSnapshotId").GetString());
        Assert.Single(projectBody.RootElement.GetProperty("applications").EnumerateArray());
        Assert.Single(projectBody.RootElement.GetProperty("snapshots").EnumerateArray());
        Assert.Equal(
            topologyId,
            projectBody.RootElement.GetProperty("applications")[0].GetProperty("topologyId").GetString());

        using var queryProjectResponse = await _client.GetAsync($"/api/automation-projects/{projectId}");
        using var queryProjectBody = await ReadJsonAsync(queryProjectResponse);

        Assert.Equal(HttpStatusCode.OK, queryProjectResponse.StatusCode);
        Assert.Equal(projectId, queryProjectBody.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(snapshotId, queryProjectBody.RootElement.GetProperty("activeSnapshotId").GetString());
    }

    [Fact]
    public async Task PublishedProjectSnapshotCanStartRuntimeSession()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-runtime-snapshot-{suffix}";
        var applicationId = $"application-runtime-snapshot-{suffix}";
        var topologyId = $"topology-runtime-snapshot-{suffix}";
        var processDefinitionId = $"process-runtime-snapshot-{suffix}";
        var recipeId = $"recipe-runtime-snapshot-{suffix}";
        var stationProfileId = $"station-runtime-snapshot-{suffix}";
        var engineeringProjectId = $"engineering-runtime-snapshot-{suffix}";
        var configurationSnapshotId = $"configuration-runtime-snapshot-{suffix}";
        var snapshotId = $"snapshot-runtime-snapshot-{suffix}";

        using var createDefinitionResponse = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateRuntimeProcessDefinitionRequest(processDefinitionId));
        using var publishDefinitionResponse = await _client.PostAsync(
            $"/api/process-definitions/{processDefinitionId}/publish",
            content: null);
        await CreatePublishedEngineeringSnapshotAsync(
            recipeId,
            stationProfileId,
            engineeringProjectId,
            configurationSnapshotId,
            processDefinitionId);

        using var createProjectResponse = await _client.PostAsJsonAsync(
            "/api/automation-projects",
            new
            {
                projectId,
                displayName = "Runtime Snapshot Project",
                projectPath = $"C:/OpenLineOps/Projects/{projectId}"
            });
        using var addApplicationResponse = await _client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/applications",
            new
            {
                applicationId,
                displayName = "Runtime Application"
            });
        using var linkTopologyResponse = await _client.PutAsJsonAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/topology",
            new
            {
                topologyId
            });
        using var linkProcessResponse = await _client.PutAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/process-definitions/{processDefinitionId}",
            content: null);
        using var publishSnapshotResponse = await _client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/snapshots",
            new
            {
                snapshotId,
                applicationId,
                topologyId,
                processDefinitionId,
                processVersionId = "packaging-line-eol@1.0.0",
                configurationSnapshotId,
                capabilityBindings = new[]
                {
                    new
                    {
                        capabilityId = "device.scanner",
                        bindingId = $"binding-runtime-snapshot-{suffix}",
                        providerKind = "Simulator",
                        providerKey = "simulator.scanner"
                    }
                },
                targetReferences = new[]
                {
                    new
                    {
                        kind = "EquipmentNode",
                        targetId = $"station-runtime-snapshot-{suffix}"
                    }
                },
                blockVersionIds = SnapshotBlockVersionIds
            });
        using var startResponse = await _client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/snapshots/{snapshotId}/runtime-sessions",
            new
            {
                serialNumber = $"SN-{suffix}",
                batchId = $"BATCH-{suffix}",
                fixtureId = $"FIX-{suffix}",
                deviceId = $"DEV-{suffix}",
                actorId = "api-test"
            });
        using var startBody = await ReadJsonAsync(startResponse);
        var sessionId = startBody.RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, createDefinitionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishDefinitionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createProjectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, addApplicationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, linkTopologyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, linkProcessResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, publishSnapshotResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        Assert.Equal(snapshotId, startBody.RootElement.GetProperty("snapshotId").GetString());
        Assert.Equal(configurationSnapshotId, startBody.RootElement.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal("Completed", startBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, startBody.RootElement.GetProperty("completedSteps").GetInt32());

        using var querySessionResponse = await _client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionBody = await ReadJsonAsync(querySessionResponse);

        Assert.Equal(HttpStatusCode.OK, querySessionResponse.StatusCode);
        Assert.Equal(processDefinitionId, sessionBody.RootElement.GetProperty("processDefinitionId").GetString());
        Assert.Equal(configurationSnapshotId, sessionBody.RootElement.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal($"SN-{suffix}", sessionBody.RootElement.GetProperty("serialNumber").GetString());
        Assert.Equal("api-test", sessionBody.RootElement.GetProperty("actorId").GetString());
    }

    [Fact]
    public async Task SiteLayoutRejectsMissingTopologyTarget()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topologyId = $"topology-layout-missing-target-{suffix}";
        var rootNodeId = $"site-layout-{suffix}";
        var layoutId = $"layout-missing-target-{suffix}";

        using var createTopologyResponse = await _client.PostAsJsonAsync(
            "/api/automation-topologies",
            new
            {
                topologyId,
                displayName = "Layout Validation Topology"
            });
        await AddNodeAsync(topologyId, rootNodeId, parentNodeId: null, kind: "Site", displayName: "Main Site");
        using var createLayoutResponse = await _client.PostAsJsonAsync(
            "/api/site-layouts",
            new
            {
                layoutId,
                topologyId,
                displayName = "Layout",
                canvasWidth = 800,
                canvasHeight = 600,
                units = "mm"
            });
        using var addMissingTargetResponse = await _client.PostAsJsonAsync(
            $"/api/site-layouts/{layoutId}/elements",
            new
            {
                elementId = $"layout-missing-slot-{suffix}",
                kind = "SlotShape",
                targetKind = "Slot",
                targetId = $"missing-slot-{suffix}",
                x = 10,
                y = 20,
                width = 30,
                height = 40,
                rotationDegrees = 0,
                layerId = "slots",
                label = "Missing"
            });
        using var body = await ReadJsonAsync(addMissingTargetResponse);

        Assert.Equal(HttpStatusCode.Created, createTopologyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createLayoutResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, addMissingTargetResponse.StatusCode);
        Assert.Equal("Validation.Topology.LayoutTargetMissing", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DuplicateAutomationProjectReturnsConflict()
    {
        var projectId = $"project-duplicate-{Guid.NewGuid():N}";
        var request = new
        {
            projectId,
            displayName = "Duplicate Project",
            projectPath = $"C:/OpenLineOps/Projects/{projectId}"
        };

        using var firstResponse = await _client.PostAsJsonAsync("/api/automation-projects", request);
        using var secondResponse = await _client.PostAsJsonAsync("/api/automation-projects", request);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
    }

    private async Task AddNodeAsync(
        string topologyId,
        string nodeId,
        string? parentNodeId,
        string kind,
        string displayName)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/automation-topologies/{topologyId}/nodes",
            new
            {
                nodeId,
                parentNodeId,
                kind,
                displayName
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task CreatePublishedEngineeringSnapshotAsync(
        string recipeId,
        string stationProfileId,
        string projectId,
        string configurationSnapshotId,
        string processDefinitionId)
    {
        var workspaceId = $"workspace-{projectId}";

        using var createRecipeResponse = await _client.PostAsJsonAsync(
            "/api/engineering/recipes",
            new
            {
                recipeId,
                versionId = $"{recipeId}@1.0.0",
                displayName = "Runtime Snapshot Recipe",
                parameters = new[]
                {
                    new
                    {
                        key = "scan.mode",
                        value = "api-test"
                    }
                }
            });
        using var publishRecipeResponse = await _client.PostAsync(
            $"/api/engineering/recipes/{recipeId}/publish",
            content: null);
        using var createStationResponse = await _client.PostAsJsonAsync(
            "/api/engineering/station-profiles",
            new
            {
                stationProfileId,
                displayName = "Runtime Snapshot Station",
                deviceBindings = new[]
                {
                    new
                    {
                        deviceBindingId = "scanner-primary",
                        capabilityId = "device.scanner",
                        deviceKey = "scanner-01"
                    }
                }
            });
        using var createWorkspaceResponse = await _client.PostAsJsonAsync(
            "/api/engineering/workspaces",
            new
            {
                workspaceId,
                displayName = "Runtime Snapshot Workspace"
            });
        using var createProjectResponse = await _client.PostAsJsonAsync(
            "/api/engineering/projects",
            new
            {
                projectId,
                workspaceId,
                displayName = "Runtime Snapshot Engineering Project"
            });
        using var publishSnapshotResponse = await _client.PostAsJsonAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots",
            new
            {
                snapshotId = configurationSnapshotId,
                processDefinitionId,
                processVersionId = "packaging-line-eol@1.0.0",
                recipeId,
                stationProfileId
            });

        Assert.Equal(HttpStatusCode.Created, createRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createStationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createProjectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, publishSnapshotResponse.StatusCode);
    }

    private static object CreateRuntimeProcessDefinitionRequest(string processDefinitionId)
    {
        return new
        {
            processDefinitionId,
            versionId = "packaging-line-eol@1.0.0",
            displayName = "Runtime Snapshot Process",
            nodes = new[]
            {
                new
                {
                    nodeId = "start",
                    kind = "Start",
                    displayName = "Start",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                },
                new
                {
                    nodeId = "inspect",
                    kind = "Command",
                    displayName = "Inspect",
                    requiredCapability = (string?)"device.scanner",
                    commandName = (string?)"Inspect",
                    timeoutSeconds = (int?)30,
                    inputPayload = (string?)"scan-ok"
                },
                new
                {
                    nodeId = "end",
                    kind = "End",
                    displayName = "End",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "start-to-inspect",
                    fromNodeId = "start",
                    toNodeId = "inspect",
                    label = (string?)null
                },
                new
                {
                    transitionId = "inspect-to-end",
                    fromNodeId = "inspect",
                    toNodeId = "end",
                    label = (string?)"ok"
                }
            }
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
