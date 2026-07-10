using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiCreateProcessDefinitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessDefinitionRequest;
using ApiCreateProcessNodeRequest = OpenLineOps.Processes.Api.Models.CreateProcessNodeRequest;
using ApiCreateProcessTransitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessTransitionRequest;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectTopologyColdRestartApiTests : IDisposable
{
    private const string DefaultBlocklyWorkspaceJson = """{"blocks":{"languageVersion":0}}""";
    private const string ReplacementBlocklyWorkspaceJson =
        """{"blocks":{"languageVersion":0,"blocks":[{"type":"flow_wait","id":"application-b-wait"}]}}""";
    private const string ReplacementPythonSource =
        "result = {'application': 'B', 'revision': 2}\n";

    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-topology-api-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProjectScopedTopologyAndLayoutSurviveHostRestartFolderMoveAndIsolateApplications()
    {
        const string projectId = "project.cold-restart";
        const string applicationA = "application.a";
        const string applicationB = "application.b";
        const string topologyId = "topology.main";
        const string layoutId = "layout.main";
        const string processDefinitionId = "process.main";
        const string customBlockType = "user_shared_fixture_action";
        const string engineeringWorkspaceId = "workspace.main";
        const string engineeringProjectId = "engineering.main";
        const string recipeId = "recipe.main";
        const string stationProfileId = "station.main";
        const string configurationSnapshotId = "snapshot.main";
        const string projectSnapshotA = "release.application-a";
        const string projectSnapshotB = "release.application-b";

        using (var firstFactory = new WebApplicationFactory<Program>())
        using (var client = firstFactory.CreateClient())
        {
            using var createWorkspace = await client.PostAsJsonAsync(
                "/api/automation-project-workspaces",
                new
                {
                    projectId,
                    displayName = "Cold Restart Project",
                    projectPath = _projectDirectory,
                    defaultApplicationId = applicationA,
                    defaultApplicationName = "Application A"
                });
            Assert.Equal(HttpStatusCode.Created, createWorkspace.StatusCode);

            using var addApplication = await client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/applications",
                new
                {
                    applicationId = applicationB,
                    displayName = "Application B"
                });
            Assert.Equal(HttpStatusCode.OK, addApplication.StatusCode);

            await CreateApplicationTopologyAsync(
                client,
                projectId,
                applicationA,
                topologyId,
                layoutId,
                "Application A Topology",
                "Application A Site",
                elementX: 25);
            await CreateApplicationTopologyAsync(
                client,
                projectId,
                applicationB,
                topologyId,
                layoutId,
                "Application B Topology",
                "Application B Site",
                elementX: 425);
            await CreateApplicationProcessAsync(
                client,
                projectId,
                applicationA,
                processDefinitionId,
                "Application A Flow",
                "result = {'application': 'A'}\n",
                publish: true);
            await CreateApplicationProcessAsync(
                client,
                projectId,
                applicationB,
                processDefinitionId,
                "Application B Flow",
                "result = {'application': 'B'}\n",
                publish: false);

            using var replaceApplicationBProcess = await client.PutAsJsonAsync(
                ProjectProcessPath(projectId, applicationB, processDefinitionId),
                CreateApplicationProcessRequest(
                    processDefinitionId,
                    "Application B Flow Replaced",
                    ReplacementPythonSource,
                    versionId: $"{processDefinitionId}@2.0.0",
                    blocklyWorkspaceJson: ReplacementBlocklyWorkspaceJson));
            Assert.Equal(HttpStatusCode.OK, replaceApplicationBProcess.StatusCode);
            using var publishApplicationBProcess = await client.PostAsync(
                $"{ProjectProcessPath(projectId, applicationB, processDefinitionId)}/publish",
                content: null);
            Assert.Equal(HttpStatusCode.OK, publishApplicationBProcess.StatusCode);

            await RegisterApplicationBlockAsync(
                client,
                projectId,
                applicationA,
                customBlockType,
                "Application A Fixture Action",
                "application A fixture action v1",
                "automation_plan.append({'application': 'A', 'revision': 1})",
                expectedVersion: 1);
            await RegisterApplicationBlockAsync(
                client,
                projectId,
                applicationA,
                customBlockType,
                "Application A Fixture Action V2",
                "application A fixture action v2",
                "automation_plan.append({'application': 'A', 'revision': 2})",
                expectedVersion: 2);
            await RegisterApplicationBlockAsync(
                client,
                projectId,
                applicationB,
                customBlockType,
                "Application B Fixture Action",
                "application B fixture action",
                "automation_plan.append({'application': 'B', 'revision': 1})",
                expectedVersion: 1);

            await CreateApplicationEngineeringConfigurationAsync(
                client,
                projectId,
                applicationA,
                "Application A",
                engineeringWorkspaceId,
                engineeringProjectId,
                recipeId,
                $"{recipeId}@1.0.0",
                stationProfileId,
                configurationSnapshotId,
                processDefinitionId,
                $"{processDefinitionId}@1.0.0",
                recipeParameterValue: "5.1",
                capabilityId: "device.scanner",
                deviceKey: "scanner-a");
            await CreateApplicationEngineeringConfigurationAsync(
                client,
                projectId,
                applicationB,
                "Application B",
                engineeringWorkspaceId,
                engineeringProjectId,
                recipeId,
                $"{recipeId}@2.0.0",
                stationProfileId,
                configurationSnapshotId,
                processDefinitionId,
                $"{processDefinitionId}@2.0.0",
                recipeParameterValue: "7.4",
                capabilityId: "device.camera",
                deviceKey: "camera-b");

            foreach (var applicationId in new[] { applicationA, applicationB })
            {
                using var linkTopology = await client.PutAsJsonAsync(
                    $"/api/automation-projects/{projectId}/applications/{applicationId}/topology",
                    new { topologyId });
                Assert.Equal(HttpStatusCode.OK, linkTopology.StatusCode);

                using var linkProcess = await client.PutAsync(
                    $"/api/automation-projects/{projectId}/applications/{applicationId}/process-definitions/{processDefinitionId}",
                    content: null);
                Assert.Equal(HttpStatusCode.OK, linkProcess.StatusCode);
            }

            await PublishApplicationProjectSnapshotAsync(
                client,
                projectId,
                projectSnapshotA,
                applicationA,
                topologyId,
                processDefinitionId,
                $"{processDefinitionId}@1.0.0",
                configurationSnapshotId,
                blockVersionId: $"{customBlockType}@2");
            await PublishApplicationProjectSnapshotAsync(
                client,
                projectId,
                projectSnapshotB,
                applicationB,
                topologyId,
                processDefinitionId,
                $"{processDefinitionId}@2.0.0",
                configurationSnapshotId,
                blockVersionId: $"{customBlockType}@1");

            using var saveManifest = await client.PutAsync(
                $"/api/automation-projects/{projectId}/manifest",
                content: null);
            Assert.Equal(HttpStatusCode.OK, saveManifest.StatusCode);
        }

        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "topology-*.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "layout-*.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "flow.json", SearchOption.AllDirectories).Length);
        Assert.True(
            Directory.GetFiles(_projectDirectory, "workspace.*.blockly.json", SearchOption.AllDirectories).Length >= 2);
        Assert.True(
            Directory.GetFiles(_projectDirectory, "generated.*.py", SearchOption.AllDirectories).Length >= 2);
        Directory.Move(_projectDirectory, MovedProjectDirectory);

        using (var secondFactory = new WebApplicationFactory<Program>())
        using (var client = secondFactory.CreateClient())
        {
            using var openWorkspace = await client.PostAsJsonAsync(
                "/api/automation-project-workspaces/open",
                new { projectPath = MovedProjectDirectory });
            Assert.Equal(HttpStatusCode.OK, openWorkspace.StatusCode);

            using var topologyA = await GetJsonAsync(
                client,
                ProjectTopologyPath(projectId, applicationA, topologyId));
            using var topologyB = await GetJsonAsync(
                client,
                ProjectTopologyPath(projectId, applicationB, topologyId));
            using var layoutA = await GetJsonAsync(
                client,
                ProjectLayoutPath(projectId, applicationA, layoutId));
            using var layoutB = await GetJsonAsync(
                client,
                ProjectLayoutPath(projectId, applicationB, layoutId));
            using var processA = await GetJsonAsync(
                client,
                ProjectProcessPath(projectId, applicationA, processDefinitionId));
            using var processB = await GetJsonAsync(
                client,
                ProjectProcessPath(projectId, applicationB, processDefinitionId));

            Assert.Equal("Application A Topology", topologyA.RootElement.GetProperty("displayName").GetString());
            Assert.Equal("Application A Site", topologyA.RootElement.GetProperty("nodes")[0].GetProperty("displayName").GetString());
            Assert.Equal("Application B Topology", topologyB.RootElement.GetProperty("displayName").GetString());
            Assert.Equal("Application B Site", topologyB.RootElement.GetProperty("nodes")[0].GetProperty("displayName").GetString());
            Assert.Equal(25, layoutA.RootElement.GetProperty("elements")[0].GetProperty("x").GetDouble());
            Assert.Equal(425, layoutB.RootElement.GetProperty("elements")[0].GetProperty("x").GetDouble());
            Assert.Equal("Application A Flow", processA.RootElement.GetProperty("displayName").GetString());
            Assert.Equal($"{processDefinitionId}@1.0.0", processA.RootElement.GetProperty("versionId").GetString());
            Assert.Equal("Published", processA.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "result = {'application': 'A'}\n",
                processA.RootElement.GetProperty("nodes")[1].GetProperty("scriptSourceCode").GetString());
            Assert.Equal(DefaultBlocklyWorkspaceJson,
                processA.RootElement.GetProperty("nodes")[1].GetProperty("blocklyWorkspaceJson").GetString());
            Assert.Equal("Application B Flow Replaced", processB.RootElement.GetProperty("displayName").GetString());
            Assert.Equal($"{processDefinitionId}@2.0.0", processB.RootElement.GetProperty("versionId").GetString());
            Assert.Equal("Published", processB.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                ReplacementPythonSource,
                processB.RootElement.GetProperty("nodes")[1].GetProperty("scriptSourceCode").GetString());
            Assert.Equal(
                ReplacementBlocklyWorkspaceJson,
                processB.RootElement.GetProperty("nodes")[1].GetProperty("blocklyWorkspaceJson").GetString());

            using var blocksAResponse = await client.GetAsync(ProjectBlocksPath(projectId, applicationA));
            using var blocksA = await ReadJsonAsync(blocksAResponse);
            using var blocksBResponse = await client.GetAsync(ProjectBlocksPath(projectId, applicationB));
            using var blocksB = await ReadJsonAsync(blocksBResponse);
            using var blockVersionsAResponse = await client.GetAsync(
                ProjectBlockVersionsPath(projectId, applicationA, customBlockType));
            using var blockVersionsA = await ReadJsonAsync(blockVersionsAResponse);
            using var blockVersionsBResponse = await client.GetAsync(
                ProjectBlockVersionsPath(projectId, applicationB, customBlockType));
            using var blockVersionsB = await ReadJsonAsync(blockVersionsBResponse);

            Assert.Equal(HttpStatusCode.OK, blocksAResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, blocksBResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, blockVersionsAResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, blockVersionsBResponse.StatusCode);

            var customBlockA = Assert.Single(
                blocksA.RootElement.EnumerateArray(),
                block => block.GetProperty("blockType").GetString() == customBlockType);
            Assert.False(customBlockA.GetProperty("isBuiltIn").GetBoolean());
            Assert.Equal(2, customBlockA.GetProperty("version").GetInt32());
            Assert.Equal("Application A Fixture Action V2", customBlockA.GetProperty("displayName").GetString());
            Assert.Equal(
                "application A fixture action v2",
                customBlockA.GetProperty("blocklyJson").GetProperty("message0").GetString());
            Assert.Contains("'application': 'A'", customBlockA.GetProperty("pythonCodeTemplate").GetString());

            var customBlockB = Assert.Single(
                blocksB.RootElement.EnumerateArray(),
                block => block.GetProperty("blockType").GetString() == customBlockType);
            Assert.False(customBlockB.GetProperty("isBuiltIn").GetBoolean());
            Assert.Equal(1, customBlockB.GetProperty("version").GetInt32());
            Assert.Equal("Application B Fixture Action", customBlockB.GetProperty("displayName").GetString());
            Assert.Equal(
                "application B fixture action",
                customBlockB.GetProperty("blocklyJson").GetProperty("message0").GetString());
            Assert.Contains("'application': 'B'", customBlockB.GetProperty("pythonCodeTemplate").GetString());

            Assert.Contains(blocksA.RootElement.EnumerateArray(), IsMoveAxisBuiltInBlock);
            Assert.Contains(blocksB.RootElement.EnumerateArray(), IsMoveAxisBuiltInBlock);
            Assert.Collection(
                blockVersionsA.RootElement.EnumerateArray(),
                block =>
                {
                    Assert.Equal(2, block.GetProperty("version").GetInt32());
                    Assert.Equal("Application A Fixture Action V2", block.GetProperty("displayName").GetString());
                },
                block =>
                {
                    Assert.Equal(1, block.GetProperty("version").GetInt32());
                    Assert.Equal("Application A Fixture Action", block.GetProperty("displayName").GetString());
                });
            Assert.Collection(
                blockVersionsB.RootElement.EnumerateArray(),
                block =>
                {
                    Assert.Equal(1, block.GetProperty("version").GetInt32());
                    Assert.Equal("Application B Fixture Action", block.GetProperty("displayName").GetString());
                });

            var engineeringBaseA = ProjectEngineeringPath(projectId, applicationA);
            var engineeringBaseB = ProjectEngineeringPath(projectId, applicationB);
            using var engineeringWorkspaceA = await GetJsonAsync(
                client,
                $"{engineeringBaseA}/workspaces/{engineeringWorkspaceId}");
            using var engineeringWorkspaceB = await GetJsonAsync(
                client,
                $"{engineeringBaseB}/workspaces/{engineeringWorkspaceId}");
            using var recipeA = await GetJsonAsync(client, $"{engineeringBaseA}/recipes/{recipeId}");
            using var recipeB = await GetJsonAsync(client, $"{engineeringBaseB}/recipes/{recipeId}");
            using var stationA = await GetJsonAsync(
                client,
                $"{engineeringBaseA}/station-profiles/{stationProfileId}");
            using var stationB = await GetJsonAsync(
                client,
                $"{engineeringBaseB}/station-profiles/{stationProfileId}");
            using var engineeringProjectA = await GetJsonAsync(
                client,
                $"{engineeringBaseA}/projects/{engineeringProjectId}");
            using var engineeringProjectB = await GetJsonAsync(
                client,
                $"{engineeringBaseB}/projects/{engineeringProjectId}");

            AssertEngineeringConfiguration(
                engineeringWorkspaceA,
                recipeA,
                stationA,
                engineeringProjectA,
                "Application A",
                engineeringWorkspaceId,
                engineeringProjectId,
                recipeId,
                $"{recipeId}@1.0.0",
                stationProfileId,
                configurationSnapshotId,
                processDefinitionId,
                $"{processDefinitionId}@1.0.0",
                recipeParameterValue: "5.1",
                capabilityId: "device.scanner",
                deviceKey: "scanner-a");
            AssertEngineeringConfiguration(
                engineeringWorkspaceB,
                recipeB,
                stationB,
                engineeringProjectB,
                "Application B",
                engineeringWorkspaceId,
                engineeringProjectId,
                recipeId,
                $"{recipeId}@2.0.0",
                stationProfileId,
                configurationSnapshotId,
                processDefinitionId,
                $"{processDefinitionId}@2.0.0",
                recipeParameterValue: "7.4",
                capabilityId: "device.camera",
                deviceKey: "camera-b");

            await AssertProjectSnapshotStartsWithScopedConfigurationAsync(
                client,
                projectId,
                projectSnapshotA,
                applicationA,
                topologyId,
                processDefinitionId,
                $"{processDefinitionId}@1.0.0",
                configurationSnapshotId,
                $"{recipeId}@1.0.0");
            await AssertProjectSnapshotStartsWithScopedConfigurationAsync(
                client,
                projectId,
                projectSnapshotB,
                applicationB,
                topologyId,
                processDefinitionId,
                $"{processDefinitionId}@2.0.0",
                configurationSnapshotId,
                $"{recipeId}@2.0.0");

            using var listAResponse = await client.GetAsync(ProjectTopologiesPath(projectId, applicationA));
            using var listA = await ReadJsonAsync(listAResponse);
            using var listBResponse = await client.GetAsync(ProjectTopologiesPath(projectId, applicationB));
            using var listB = await ReadJsonAsync(listBResponse);

            Assert.Equal(HttpStatusCode.OK, listAResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, listBResponse.StatusCode);
            Assert.Single(listA.RootElement.EnumerateArray());
            Assert.Single(listB.RootElement.EnumerateArray());

            using var processListAResponse = await client.GetAsync(ProjectProcessesPath(projectId, applicationA));
            using var processListA = await ReadJsonAsync(processListAResponse);
            using var processListBResponse = await client.GetAsync(ProjectProcessesPath(projectId, applicationB));
            using var processListB = await ReadJsonAsync(processListBResponse);
            Assert.Equal(HttpStatusCode.OK, processListAResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, processListBResponse.StatusCode);
            Assert.Single(processListA.RootElement.EnumerateArray());
            Assert.Single(processListB.RootElement.EnumerateArray());
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDirectory))
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }

        if (Directory.Exists(MovedProjectDirectory))
        {
            Directory.Delete(MovedProjectDirectory, recursive: true);
        }
    }

    private static async Task CreateApplicationTopologyAsync(
        HttpClient client,
        string projectId,
        string applicationId,
        string topologyId,
        string layoutId,
        string topologyName,
        string nodeName,
        double elementX)
    {
        var topologiesPath = ProjectTopologiesPath(projectId, applicationId);
        var layoutsPath = ProjectLayoutsPath(projectId, applicationId);

        using var createTopology = await client.PostAsJsonAsync(
            topologiesPath,
            new { topologyId, displayName = topologyName });
        Assert.Equal(HttpStatusCode.Created, createTopology.StatusCode);

        using var addNode = await client.PostAsJsonAsync(
            $"{topologiesPath}/{topologyId}/nodes",
            new
            {
                nodeId = "site.main",
                parentNodeId = (string?)null,
                kind = "Site",
                displayName = nodeName
            });
        Assert.Equal(HttpStatusCode.OK, addNode.StatusCode);

        using var createLayout = await client.PostAsJsonAsync(
            layoutsPath,
            new
            {
                layoutId,
                topologyId,
                displayName = $"{topologyName} Layout",
                canvasWidth = 800,
                canvasHeight = 600,
                units = "mm"
            });
        Assert.Equal(HttpStatusCode.Created, createLayout.StatusCode);

        using var addElement = await client.PostAsJsonAsync(
            $"{layoutsPath}/{layoutId}/elements",
            new
            {
                elementId = "element.site",
                kind = "NodeShape",
                targetKind = "EquipmentNode",
                targetId = "site.main",
                x = elementX,
                y = 40,
                width = 100,
                height = 80,
                rotationDegrees = 0,
                layerId = "equipment",
                label = nodeName
            });
        Assert.Equal(HttpStatusCode.OK, addElement.StatusCode);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var document = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return document;
    }

    private static async Task CreateApplicationProcessAsync(
        HttpClient client,
        string projectId,
        string applicationId,
        string processDefinitionId,
        string displayName,
        string sourceCode,
        bool publish)
    {
        var processesPath = ProjectProcessesPath(projectId, applicationId);
        using var createProcess = await client.PostAsJsonAsync(
            processesPath,
            CreateApplicationProcessRequest(processDefinitionId, displayName, sourceCode));
        Assert.Equal(HttpStatusCode.Created, createProcess.StatusCode);

        if (publish)
        {
            using var publishProcess = await client.PostAsync(
                $"{processesPath}/{processDefinitionId}/publish",
                content: null);
            Assert.Equal(HttpStatusCode.OK, publishProcess.StatusCode);
        }
    }

    private static async Task RegisterApplicationBlockAsync(
        HttpClient client,
        string projectId,
        string applicationId,
        string blockType,
        string displayName,
        string message,
        string pythonCodeTemplate,
        int expectedVersion)
    {
        using var response = await client.PostAsJsonAsync(
            ProjectBlocksPath(projectId, applicationId),
            new
            {
                blockType,
                category = "Fixture",
                displayName,
                blocklyJson = new
                {
                    type = blockType,
                    message0 = message,
                    previousStatement = (string?)null,
                    nextStatement = (string?)null
                },
                pythonCodeTemplate
            });
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(blockType, body.RootElement.GetProperty("blockType").GetString());
        Assert.Equal(expectedVersion, body.RootElement.GetProperty("version").GetInt32());
        Assert.False(body.RootElement.GetProperty("isBuiltIn").GetBoolean());
    }

    private static async Task CreateApplicationEngineeringConfigurationAsync(
        HttpClient client,
        string projectId,
        string applicationId,
        string applicationName,
        string workspaceId,
        string engineeringProjectId,
        string recipeId,
        string recipeVersionId,
        string stationProfileId,
        string snapshotId,
        string processDefinitionId,
        string processVersionId,
        string recipeParameterValue,
        string capabilityId,
        string deviceKey)
    {
        var engineeringBase = ProjectEngineeringPath(projectId, applicationId);
        using var workspaceResponse = await client.PostAsJsonAsync(
            $"{engineeringBase}/workspaces",
            new
            {
                workspaceId,
                displayName = $"{applicationName} Engineering Workspace"
            });
        using var recipeResponse = await client.PostAsJsonAsync(
            $"{engineeringBase}/recipes",
            new
            {
                recipeId,
                versionId = recipeVersionId,
                displayName = $"{applicationName} Recipe",
                parameters = new[]
                {
                    new { key = "voltage.max", value = recipeParameterValue }
                }
            });
        using var publishRecipeResponse = await client.PostAsync(
            $"{engineeringBase}/recipes/{recipeId}/publish",
            content: null);
        using var stationResponse = await client.PostAsJsonAsync(
            $"{engineeringBase}/station-profiles",
            new
            {
                stationProfileId,
                displayName = $"{applicationName} Station",
                deviceBindings = new[]
                {
                    new
                    {
                        deviceBindingId = "binding.primary",
                        capabilityId,
                        deviceKey
                    }
                }
            });
        using var projectResponse = await client.PostAsJsonAsync(
            $"{engineeringBase}/projects",
            new
            {
                projectId = engineeringProjectId,
                workspaceId,
                displayName = $"{applicationName} Engineering Project"
            });
        using var snapshotResponse = await client.PostAsJsonAsync(
            $"{engineeringBase}/projects/{engineeringProjectId}/configuration-snapshots",
            new
            {
                snapshotId,
                processDefinitionId,
                processVersionId,
                recipeId,
                stationProfileId
            });
        using var snapshotBody = await ReadJsonAsync(snapshotResponse);

        Assert.Equal(HttpStatusCode.Created, workspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, recipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, stationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, snapshotResponse.StatusCode);
        Assert.Equal(snapshotId, snapshotBody.RootElement.GetProperty("activeSnapshotId").GetString());
    }

    private static async Task PublishApplicationProjectSnapshotAsync(
        HttpClient client,
        string projectId,
        string projectSnapshotId,
        string applicationId,
        string topologyId,
        string processDefinitionId,
        string processVersionId,
        string configurationSnapshotId,
        string blockVersionId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/snapshots",
            new
            {
                snapshotId = projectSnapshotId,
                applicationId,
                topologyId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                capabilityBindings = new[]
                {
                    new
                    {
                        capabilityId = "runtime.execute",
                        bindingId = $"binding.{applicationId}",
                        providerKind = "Simulator",
                        providerKey = $"simulator.{applicationId}"
                    }
                },
                targetReferences = new[]
                {
                    new
                    {
                        kind = "EquipmentNode",
                        targetId = "site.main"
                    }
                },
                blockVersionIds = new[] { blockVersionId }
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task AssertProjectSnapshotStartsWithScopedConfigurationAsync(
        HttpClient client,
        string projectId,
        string projectSnapshotId,
        string applicationId,
        string topologyId,
        string processDefinitionId,
        string processVersionId,
        string configurationSnapshotId,
        string recipeVersionId)
    {
        using var startResponse = await client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/snapshots/{projectSnapshotId}/runtime-sessions",
            new
            {
                serialNumber = $"SN-{applicationId}",
                actorId = "scoped-engineering-test"
            });
        using var startBody = await ReadJsonAsync(startResponse);

        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        Assert.Equal(projectSnapshotId, startBody.RootElement.GetProperty("snapshotId").GetString());
        Assert.Equal(projectId, startBody.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(applicationId, startBody.RootElement.GetProperty("applicationId").GetString());
        Assert.Equal(topologyId, startBody.RootElement.GetProperty("topologyId").GetString());
        Assert.Equal(configurationSnapshotId, startBody.RootElement.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal("Completed", startBody.RootElement.GetProperty("status").GetString());

        var sessionId = startBody.RootElement.GetProperty("sessionId").GetGuid();
        using var sessionResponse = await client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var session = await ReadJsonAsync(sessionResponse);

        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        Assert.Equal(processDefinitionId, session.RootElement.GetProperty("processDefinitionId").GetString());
        Assert.Equal(processVersionId, session.RootElement.GetProperty("processVersionId").GetString());
        Assert.Equal(configurationSnapshotId, session.RootElement.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal(recipeVersionId, session.RootElement.GetProperty("recipeSnapshotId").GetString());
        Assert.Equal(projectId, session.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(applicationId, session.RootElement.GetProperty("applicationId").GetString());
        Assert.Equal(projectSnapshotId, session.RootElement.GetProperty("projectSnapshotId").GetString());
    }

    private static void AssertEngineeringConfiguration(
        JsonDocument workspace,
        JsonDocument recipe,
        JsonDocument station,
        JsonDocument engineeringProject,
        string applicationName,
        string workspaceId,
        string engineeringProjectId,
        string recipeId,
        string recipeVersionId,
        string stationProfileId,
        string snapshotId,
        string processDefinitionId,
        string processVersionId,
        string recipeParameterValue,
        string capabilityId,
        string deviceKey)
    {
        Assert.Equal(workspaceId, workspace.RootElement.GetProperty("workspaceId").GetString());
        Assert.Equal(
            $"{applicationName} Engineering Workspace",
            workspace.RootElement.GetProperty("displayName").GetString());
        Assert.NotEqual(default, workspace.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());

        Assert.Equal(recipeId, recipe.RootElement.GetProperty("recipeId").GetString());
        Assert.Equal(recipeVersionId, recipe.RootElement.GetProperty("versionId").GetString());
        Assert.Equal($"{applicationName} Recipe", recipe.RootElement.GetProperty("displayName").GetString());
        Assert.Equal("Published", recipe.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(default, recipe.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.NotEqual(default, recipe.RootElement.GetProperty("publishedAtUtc").GetDateTimeOffset());
        var parameter = Assert.Single(recipe.RootElement.GetProperty("parameters").EnumerateArray());
        Assert.Equal("voltage.max", parameter.GetProperty("key").GetString());
        Assert.Equal(recipeParameterValue, parameter.GetProperty("value").GetString());

        Assert.Equal(stationProfileId, station.RootElement.GetProperty("stationProfileId").GetString());
        Assert.Equal($"{applicationName} Station", station.RootElement.GetProperty("displayName").GetString());
        var binding = Assert.Single(station.RootElement.GetProperty("deviceBindings").EnumerateArray());
        Assert.Equal("binding.primary", binding.GetProperty("deviceBindingId").GetString());
        Assert.Equal(capabilityId, binding.GetProperty("capabilityId").GetString());
        Assert.Equal(deviceKey, binding.GetProperty("deviceKey").GetString());

        Assert.Equal(engineeringProjectId, engineeringProject.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(workspaceId, engineeringProject.RootElement.GetProperty("workspaceId").GetString());
        Assert.Equal(
            $"{applicationName} Engineering Project",
            engineeringProject.RootElement.GetProperty("displayName").GetString());
        Assert.NotEqual(default, engineeringProject.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.Equal(snapshotId, engineeringProject.RootElement.GetProperty("activeSnapshotId").GetString());
        var snapshot = Assert.Single(engineeringProject.RootElement.GetProperty("snapshots").EnumerateArray());
        Assert.Equal(snapshotId, snapshot.GetProperty("snapshotId").GetString());
        Assert.Equal(engineeringProjectId, snapshot.GetProperty("projectId").GetString());
        Assert.Equal(processDefinitionId, snapshot.GetProperty("processDefinitionId").GetString());
        Assert.Equal(processVersionId, snapshot.GetProperty("processVersionId").GetString());
        Assert.Equal(recipeId, snapshot.GetProperty("recipeId").GetString());
        Assert.Equal(recipeVersionId, snapshot.GetProperty("recipeVersionId").GetString());
        Assert.Equal(stationProfileId, snapshot.GetProperty("stationProfileId").GetString());
        Assert.Equal("Published", snapshot.GetProperty("status").GetString());
        Assert.NotEqual(default, snapshot.GetProperty("publishedAtUtc").GetDateTimeOffset());
        var snapshotBinding = Assert.Single(snapshot.GetProperty("deviceBindings").EnumerateArray());
        Assert.Equal("binding.primary", snapshotBinding.GetProperty("deviceBindingId").GetString());
        Assert.Equal(capabilityId, snapshotBinding.GetProperty("capabilityId").GetString());
        Assert.Equal(deviceKey, snapshotBinding.GetProperty("deviceKey").GetString());
    }

    private static bool IsMoveAxisBuiltInBlock(JsonElement block)
    {
        return block.GetProperty("blockType").GetString() == "openlineops_move_axis"
            && block.GetProperty("isBuiltIn").GetBoolean()
            && block.GetProperty("version").GetInt32() == 1;
    }

    private static ApiCreateProcessDefinitionRequest CreateApplicationProcessRequest(
        string processDefinitionId,
        string displayName,
        string sourceCode,
        string? versionId = null,
        string blocklyWorkspaceJson = DefaultBlocklyWorkspaceJson)
    {
        return new ApiCreateProcessDefinitionRequest(
            processDefinitionId,
            versionId ?? $"{processDefinitionId}@1.0.0",
            displayName,
            [
                new ApiCreateProcessNodeRequest(
                    "start",
                    "Start",
                    "Start",
                    RequiredCapability: null,
                    CommandName: null,
                    TimeoutSeconds: null,
                    InputPayload: null,
                    ScriptEditorMode: null,
                    BlocklyWorkspaceJson: null,
                    ScriptSourceCode: null,
                    ScriptVersion: null),
                new ApiCreateProcessNodeRequest(
                    "script",
                    "PythonScript",
                    "Blockly Script",
                    RequiredCapability: null,
                    CommandName: null,
                    TimeoutSeconds: 10,
                    InputPayload: null,
                    ScriptEditorMode: "Blockly",
                    BlocklyWorkspaceJson: blocklyWorkspaceJson,
                    ScriptSourceCode: sourceCode,
                    ScriptVersion: "1"),
                new ApiCreateProcessNodeRequest(
                    "end",
                    "End",
                    "End",
                    RequiredCapability: null,
                    CommandName: null,
                    TimeoutSeconds: null,
                    InputPayload: null,
                    ScriptEditorMode: null,
                    BlocklyWorkspaceJson: null,
                    ScriptSourceCode: null,
                    ScriptVersion: null)
            ],
            [
                new ApiCreateProcessTransitionRequest(
                    "start-to-script",
                    "start",
                    "script",
                    Label: null,
                    LoopPolicy: null,
                    MaxTraversals: null),
                new ApiCreateProcessTransitionRequest(
                    "script-to-end",
                    "script",
                    "end",
                    Label: null,
                    LoopPolicy: null,
                    MaxTraversals: null)
            ]);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static string ProjectTopologiesPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/topologies";
    }

    private static string ProjectTopologyPath(string projectId, string applicationId, string topologyId)
    {
        return $"{ProjectTopologiesPath(projectId, applicationId)}/{topologyId}";
    }

    private static string ProjectLayoutsPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/layouts";
    }

    private static string ProjectLayoutPath(string projectId, string applicationId, string layoutId)
    {
        return $"{ProjectLayoutsPath(projectId, applicationId)}/{layoutId}";
    }

    private static string ProjectProcessesPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/processes";
    }

    private static string ProjectProcessPath(
        string projectId,
        string applicationId,
        string processDefinitionId)
    {
        return $"{ProjectProcessesPath(projectId, applicationId)}/{processDefinitionId}";
    }

    private static string ProjectBlocksPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/process-blocks";
    }

    private static string ProjectBlockVersionsPath(
        string projectId,
        string applicationId,
        string blockType)
    {
        return $"{ProjectBlocksPath(projectId, applicationId)}/{blockType}/versions";
    }

    private static string ProjectEngineeringPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/engineering";
    }

    private string MovedProjectDirectory => $"{_projectDirectory}-moved";
}
