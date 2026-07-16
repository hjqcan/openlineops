using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenLineOps.Processes.Application.Scripting;
using ApiCreateProcessDefinitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessDefinitionRequest;
using ApiCreateProcessNodeRequest = OpenLineOps.Processes.Api.Models.CreateProcessNodeRequest;
using ApiCreateProcessTransitionRequest = OpenLineOps.Processes.Api.Models.CreateProcessTransitionRequest;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectTopologyColdRestartApiTests : IDisposable
{
    private const string DefaultBlocklyWorkspaceJson =
        """{"blocks":{"languageVersion":0,"blocks":[{"type":"user_shared_fixture_action","id":"application-a-fixture","fields":{"DURATION_MS":100}}]}}""";
    private const string ReplacementBlocklyWorkspaceJson =
        """{"blocks":{"languageVersion":0,"blocks":[{"type":"user_shared_fixture_action","id":"application-b-fixture","fields":{"DURATION_MS":200}}]}}""";
    private const string ReplacementPythonSource =
        "result = {'application': 'B', 'revision': 2}\n";

    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-topology-api-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _stationPackageDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-topology-station-packages",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProjectScopedTopologyAndLayoutSurviveHostRestartFolderMoveAndIsolateApplications()
    {
        const string projectId = "project.cold-restart";
        const string applicationA = "application.a";
        const string applicationB = "application.b";
        const string topologyId = "topology.main";
        const string productionLineDefinitionId = "line.main";
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
        PublishedProjectRelease? publishedReleaseA = null;
        PublishedProjectRelease? publishedReleaseB = null;

        using (var firstFactory = new ScriptWorkerWebApplicationFactory(
                   _stationPackageDirectory))
        using (var client = firstFactory.CreateAuthenticatedClient())
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
                capabilityId: "device.scanner",
                elementX: 25);
            await CreateApplicationTopologyAsync(
                client,
                projectId,
                applicationB,
                topologyId,
                layoutId,
                "Application B Topology",
                "Application B Site",
                capabilityId: "device.camera",
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

            using var replaceApplicationBProcess = await client.PutEditorAsync(
                ProjectProcessPath(projectId, applicationB, processDefinitionId),
                ProjectProcessPath(projectId, applicationB, processDefinitionId),
                CreateApplicationProcessRequest(
                    processDefinitionId,
                    "Application B Flow Replaced",
                    ReplacementPythonSource,
                    versionId: $"{processDefinitionId}@2.0.0",
                    blocklyWorkspaceJson: ReplacementBlocklyWorkspaceJson));
            Assert.Equal(HttpStatusCode.OK, replaceApplicationBProcess.StatusCode);
            using var publishApplicationBProcess = await client.PostEditorAsync<object?>(
                ProjectProcessPath(projectId, applicationB, processDefinitionId),
                $"{ProjectProcessPath(projectId, applicationB, processDefinitionId)}/publish",
                null);
            Assert.Equal(HttpStatusCode.OK, publishApplicationBProcess.StatusCode);

            await RegisterApplicationBlockAsync(
                client,
                projectId,
                applicationA,
                customBlockType,
                "Application A Fixture Action",
                "application A fixture action initial",
                expectedVersion: 1);
            await RegisterApplicationBlockAsync(
                client,
                projectId,
                applicationA,
                customBlockType,
                "Application A Fixture Action Revised",
                "application A fixture action revised",
                expectedVersion: 2);
            await RegisterApplicationBlockAsync(
                client,
                projectId,
                applicationB,
                customBlockType,
                "Application B Fixture Action",
                "application B fixture action",
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
                await CreateApplicationProductionLineAsync(
                    client,
                    projectId,
                    applicationId,
                    productionLineDefinitionId,
                    topologyId,
                    processDefinitionId,
                    configurationSnapshotId);

                using var linkTopology = await client.PutAsJsonAsync(
                    $"/api/automation-projects/{projectId}/applications/{applicationId}/topology",
                    new { topologyId });
                Assert.Equal(HttpStatusCode.OK, linkTopology.StatusCode);

                using var linkProcess = await client.PutAsync(
                    $"/api/automation-projects/{projectId}/applications/{applicationId}/process-definitions/{processDefinitionId}",
                    content: null);
                Assert.Equal(HttpStatusCode.OK, linkProcess.StatusCode);
            }

            publishedReleaseA = await PublishApplicationProjectSnapshotAsync(
                client,
                projectId,
                projectSnapshotA,
                applicationA,
                productionLineDefinitionId,
                _projectDirectory);
            publishedReleaseB = await PublishApplicationProjectSnapshotAsync(
                client,
                projectId,
                projectSnapshotB,
                applicationB,
                productionLineDefinitionId,
                _projectDirectory);

            using var mutateLiveLayout = await client.PutEditorAsync(
                ProjectLayoutPath(projectId, applicationA, layoutId),
                $"{ProjectLayoutPath(projectId, applicationA, layoutId)}/elements/element.station/geometry",
                new
                {
                    x = 125,
                    y = 40,
                    width = 100,
                    height = 80,
                    rotationDegrees = 0
                });
            Assert.Equal(HttpStatusCode.OK, mutateLiveLayout.StatusCode);

            using var saveManifest = await client.PutAsync(
                $"/api/automation-projects/{projectId}/manifest",
                content: null);
            Assert.Equal(HttpStatusCode.OK, saveManifest.StatusCode);
        }

        var applicationsDirectory = Path.Combine(_projectDirectory, "applications");
        Assert.Equal(2, Directory.GetFiles(applicationsDirectory, "topology-*.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(applicationsDirectory, "layout-*.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(applicationsDirectory, "flow.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(
            Path.Combine(_projectDirectory, "releases"),
            "release.json",
            SearchOption.AllDirectories).Length);
        Assert.NotNull(publishedReleaseA);
        Assert.NotNull(publishedReleaseB);
        Assert.NotEqual(publishedReleaseA.ContentSha256, publishedReleaseB.ContentSha256);
        var frozenLayoutPath = Assert.Single(Directory.GetFiles(
            Path.GetDirectoryName(publishedReleaseA.AbsoluteManifestPath)!,
            "layout-*.json",
            SearchOption.AllDirectories));
        using (var frozenLayout = JsonDocument.Parse(File.ReadAllText(frozenLayoutPath)))
        {
            Assert.Equal(25, frozenLayout.RootElement.GetProperty("elements")[0].GetProperty("x").GetDouble());
        }
        Assert.True(
            Directory.GetFiles(_projectDirectory, "workspace.*.blockly.json", SearchOption.AllDirectories).Length >= 2);
        Assert.Empty(Directory.GetFiles(
            _projectDirectory,
            "generated.*.py",
            SearchOption.AllDirectories));
        Assert.True(
            Directory.GetFiles(_projectDirectory, "source.*.py", SearchOption.AllDirectories).Length >= 2);
        Directory.Move(_projectDirectory, MovedProjectDirectory);

        using (var secondFactory = new ScriptWorkerWebApplicationFactory(
                   _stationPackageDirectory))
        using (var client = secondFactory.CreateAuthenticatedClient())
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
            using var releasedTopologyA = await GetJsonAsync(
                client,
                $"{ProjectTopologyPath(projectId, applicationA, topologyId)}?snapshotId={projectSnapshotA}");
            using var releasedLayoutA = await GetJsonAsync(
                client,
                $"{ProjectLayoutPath(projectId, applicationA, layoutId)}?snapshotId={projectSnapshotA}");
            using var crossApplicationReleaseRead = await client.GetAsync(
                $"{ProjectLayoutPath(projectId, applicationB, layoutId)}?snapshotId={projectSnapshotA}");
            using var processA = await GetJsonAsync(
                client,
                ProjectProcessPath(projectId, applicationA, processDefinitionId));
            using var processB = await GetJsonAsync(
                client,
                ProjectProcessPath(projectId, applicationB, processDefinitionId));

            Assert.Equal("Application A Topology", topologyA.RootElement.GetProperty("displayName").GetString());
            Assert.Equal(
                "Application A Site",
                FindTopologySystem(topologyA, "station.main").GetProperty("displayName").GetString());
            Assert.Equal("Application B Topology", topologyB.RootElement.GetProperty("displayName").GetString());
            Assert.Equal(
                "Application B Site",
                FindTopologySystem(topologyB, "station.main").GetProperty("displayName").GetString());
            Assert.Equal(125, layoutA.RootElement.GetProperty("elements")[0].GetProperty("x").GetDouble());
            Assert.Equal(425, layoutB.RootElement.GetProperty("elements")[0].GetProperty("x").GetDouble());
            Assert.Equal(
                "Application A Topology",
                releasedTopologyA.RootElement.GetProperty("displayName").GetString());
            Assert.Equal(
                25,
                releasedLayoutA.RootElement.GetProperty("elements")[0].GetProperty("x").GetDouble());
            Assert.Equal(HttpStatusCode.NotFound, crossApplicationReleaseRead.StatusCode);
            Assert.True(File.Exists(Path.Combine(
                MovedProjectDirectory,
                publishedReleaseA.RelativeManifestPath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(File.Exists(Path.Combine(
                MovedProjectDirectory,
                publishedReleaseB.RelativeManifestPath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal("Application A Flow", processA.RootElement.GetProperty("displayName").GetString());
            Assert.Equal($"{processDefinitionId}@1.0.0", processA.RootElement.GetProperty("versionId").GetString());
            Assert.Equal("Published", processA.RootElement.GetProperty("status").GetString());
            var blocklyNodeA = FindProcessNode(processA, "blockly");
            var pythonNodeA = FindProcessNode(processA, "script");
            Assert.Equal("Blockly", blocklyNodeA.GetProperty("kind").GetString());
            Assert.Equal(DefaultBlocklyWorkspaceJson, blocklyNodeA.GetProperty("blocklyWorkspaceJson").GetString());
            Assert.Equal("PythonScript", pythonNodeA.GetProperty("kind").GetString());
            Assert.Equal(
                "result = {'application': 'A'}\n",
                pythonNodeA.GetProperty("scriptSourceCode").GetString());
            Assert.Equal("Application B Flow Replaced", processB.RootElement.GetProperty("displayName").GetString());
            Assert.Equal($"{processDefinitionId}@2.0.0", processB.RootElement.GetProperty("versionId").GetString());
            Assert.Equal("Published", processB.RootElement.GetProperty("status").GetString());
            var blocklyNodeB = FindProcessNode(processB, "blockly");
            var pythonNodeB = FindProcessNode(processB, "script");
            Assert.Equal("Blockly", blocklyNodeB.GetProperty("kind").GetString());
            Assert.Equal(
                ReplacementBlocklyWorkspaceJson,
                blocklyNodeB.GetProperty("blocklyWorkspaceJson").GetString());
            Assert.Equal("PythonScript", pythonNodeB.GetProperty("kind").GetString());
            Assert.Equal(
                ReplacementPythonSource,
                pythonNodeB.GetProperty("scriptSourceCode").GetString());

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
            Assert.Equal("Application A Fixture Action Revised", customBlockA.GetProperty("displayName").GetString());
            Assert.Equal(
                "application A fixture action revised %1 ms",
                customBlockA.GetProperty("blocklyJson").GetProperty("message0").GetString());
            AssertFixtureActionContract(customBlockA);

            var customBlockB = Assert.Single(
                blocksB.RootElement.EnumerateArray(),
                block => block.GetProperty("blockType").GetString() == customBlockType);
            Assert.False(customBlockB.GetProperty("isBuiltIn").GetBoolean());
            Assert.Equal(1, customBlockB.GetProperty("version").GetInt32());
            Assert.Equal("Application B Fixture Action", customBlockB.GetProperty("displayName").GetString());
            Assert.Equal(
                "application B fixture action %1 ms",
                customBlockB.GetProperty("blocklyJson").GetProperty("message0").GetString());
            AssertFixtureActionContract(customBlockB);

            Assert.Contains(blocksA.RootElement.EnumerateArray(), IsMoveAxisBuiltInBlock);
            Assert.Contains(blocksB.RootElement.EnumerateArray(), IsMoveAxisBuiltInBlock);
            Assert.Collection(
                blockVersionsA.RootElement.EnumerateArray(),
                block =>
                {
                    Assert.Equal(2, block.GetProperty("version").GetInt32());
                    Assert.Equal("Application A Fixture Action Revised", block.GetProperty("displayName").GetString());
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

            await AssertProjectSnapshotRunsWithScopedConfigurationAsync(
                client,
                projectId,
                projectSnapshotA,
                applicationA,
                topologyId,
                productionLineDefinitionId,
                processDefinitionId,
                $"{processDefinitionId}@1.0.0",
                configurationSnapshotId,
                $"{recipeId}@1.0.0");
            await AssertProjectSnapshotRunsWithScopedConfigurationAsync(
                client,
                projectId,
                projectSnapshotB,
                applicationB,
                topologyId,
                productionLineDefinitionId,
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

        if (Directory.Exists(_stationPackageDirectory))
        {
            Directory.Delete(_stationPackageDirectory, recursive: true);
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
        string capabilityId,
        double elementX)
    {
        var topologiesPath = ProjectTopologiesPath(projectId, applicationId);
        var layoutsPath = ProjectLayoutsPath(projectId, applicationId);

        using var createTopology = await client.PostAsJsonAsync(
            topologiesPath,
            new { topologyId, displayName = topologyName });
        Assert.Equal(HttpStatusCode.Created, createTopology.StatusCode);

        using var addCapability = await client.PostEditorAsync(
            $"{topologiesPath}/{topologyId}",
            $"{topologiesPath}/{topologyId}/capabilities",
            new
            {
                capabilityId,
                commandName = "Execute",
                version = "1.0.0",
                inputSchema = """{"type":"object"}""",
                outputSchema = (string?)null,
                timeoutSeconds = 30,
                safetyClass = "Normal"
            });
        Assert.Equal(HttpStatusCode.OK, addCapability.StatusCode);

        using var addPythonCapability = await client.PostEditorAsync(
            $"{topologiesPath}/{topologyId}",
            $"{topologiesPath}/{topologyId}/capabilities",
            new
            {
                capabilityId = "process.python-script",
                commandName = "Execute",
                version = "1.0.0",
                inputSchema = """{"type":"object"}""",
                outputSchema = (string?)null,
                timeoutSeconds = 10,
                safetyClass = "Normal"
            });
        Assert.Equal(HttpStatusCode.OK, addPythonCapability.StatusCode);

        using var addNode = await client.PostEditorAsync(
            $"{topologiesPath}/{topologyId}",
            $"{topologiesPath}/{topologyId}/systems",
            new
            {
                systemId = "station.main",
                parentSystemId = (string?)null,
                kind = "Station",
                systemType = "test.station",
                displayName = nodeName,
                requiredCapabilityIds = Array.Empty<string>(),
                providedCapabilityIds = new[] { capabilityId, "process.python-script" },
                metadata = new Dictionary<string, string>()
            });
        Assert.Equal(HttpStatusCode.OK, addNode.StatusCode);

        using var addFlowHost = await client.PostEditorAsync(
            $"{topologiesPath}/{topologyId}",
            $"{topologiesPath}/{topologyId}/systems",
            new
            {
                systemId = "runtime.flow",
                parentSystemId = (string?)"station.main",
                kind = "System",
                systemType = "openlineops.runtime-flow-host",
                displayName = "Runtime Flow Host",
                requiredCapabilityIds = Array.Empty<string>(),
                providedCapabilityIds = Array.Empty<string>(),
                metadata = new Dictionary<string, string>()
            });
        Assert.Equal(HttpStatusCode.OK, addFlowHost.StatusCode);

        using var addDriverBinding = await client.PostEditorAsync(
            $"{topologiesPath}/{topologyId}",
            $"{topologiesPath}/{topologyId}/driver-bindings",
            new
            {
                bindingId = "binding.primary",
                ownerSystemId = "station.main",
                capabilityId,
                providerKind = "Simulator",
                providerKey = $"simulator.{applicationId}"
            });
        Assert.Equal(HttpStatusCode.OK, addDriverBinding.StatusCode);

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

        using var addElement = await client.PostEditorAsync(
            $"{layoutsPath}/{layoutId}",
            $"{layoutsPath}/{layoutId}/elements",
            new
            {
                elementId = "element.station",
                kind = "SystemShape",
                target = new { kind = "System", targetId = "station.main" },
                parentElementId = (string?)null,
                x = elementX,
                y = 40,
                width = 100,
                height = 80,
                rotationDegrees = 0,
                zIndex = 1,
                style = new Dictionary<string, string>()
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
            using var publishProcess = await client.PostEditorAsync<object?>(
                $"{processesPath}/{processDefinitionId}",
                $"{processesPath}/{processDefinitionId}/publish",
                null);
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
        int expectedVersion)
    {
        var contract = FixtureActionContractArtifact();
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
                    message0 = $"{message} %1 ms",
                    args0 = new[]
                    {
                        new
                        {
                            type = "field_number",
                            name = "DURATION_MS",
                            value = 100,
                            min = 0,
                            precision = 1
                        }
                    },
                    previousStatement = (string?)null,
                    nextStatement = (string?)null
                },
                runtimeActionContractSchemaVersion = contract.SchemaVersion,
                runtimeActionContract = JsonNode.Parse(contract.CanonicalJson)
            });
        using var body = await ReadJsonAsync(response);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected custom block registration to return 201, received {(int)response.StatusCode}: {body.RootElement.GetRawText()}");
        Assert.Equal(blockType, body.RootElement.GetProperty("blockType").GetString());
        Assert.Equal(expectedVersion, body.RootElement.GetProperty("version").GetInt32());
        Assert.False(body.RootElement.GetProperty("isBuiltIn").GetBoolean());
        AssertFixtureActionContract(body.RootElement);
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
                stationSystemId = "station.main",
                displayName = $"{applicationName} Station",
                deviceBindings = new[]
                {
                    new
                    {
                        deviceBindingId = "binding.primary",
                        ownerSystemId = "station.main",
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

    private static async Task CreateApplicationProductionLineAsync(
        HttpClient client,
        string projectId,
        string applicationId,
        string productionLineDefinitionId,
        string topologyId,
        string processDefinitionId,
        string configurationSnapshotId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines",
            new
            {
                lineDefinitionId = productionLineDefinitionId,
                displayName = $"{applicationId} Production Line",
                topologyId,
                productModel = new
                {
                    productModelId = "product.main",
                    modelCode = "MODEL-MAIN",
                    identityInputKey = "serialNumber"
                },
                entryOperationId = "operation.main",
                operations = new[]
                {
                    new
                    {
                        operationId = "operation.main",
                        displayName = "Main Operation",
                        stationSystemId = "station.main",
                        flowDefinitionId = processDefinitionId,
                        configurationSnapshotId,
                        inputMappings = Array.Empty<object>(),
                        resources = new[]
                        {
                            new
                            {
                                bindingId = "operation.main.station",
                                kind = "Station",
                                topologyTargetId = "station.main",
                                resolution = "Fixed"
                            },
                            new
                            {
                                bindingId = "operation.main.runtime-flow",
                                kind = "Device",
                                topologyTargetId = "runtime.flow",
                                resolution = "Fixed"
                            }
                        }
                    }
                },
                transitions = new[]
                {
                    new
                    {
                        transitionId = "operation.main-completed",
                        sourceOperationId = "operation.main",
                        targetOperationId = (string?)null,
                        terminalDisposition = "Completed",
                        kind = "Sequence"
                    }
                },
                lineControllerAuthorizations = Array.Empty<object>(),
                routeLayout = new
                {
                    operationPositions = new[]
                    {
                        new { operationId = "operation.main", x = 120, y = 80 }
                    }
                }
            });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Production line creation returned {(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    private static async Task<PublishedProjectRelease> PublishApplicationProjectSnapshotAsync(
        HttpClient client,
        string projectId,
        string projectSnapshotId,
        string applicationId,
        string productionLineDefinitionId,
        string projectDirectory)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/snapshots",
            new
            {
                snapshotId = projectSnapshotId,
                applicationId,
                productionLineDefinitionId
            });

        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Project release publication returned {(int)response.StatusCode} {response.StatusCode}: {responseText}");

        using var body = JsonDocument.Parse(responseText);
        var snapshot = Assert.Single(
            body.RootElement.GetProperty("snapshots").EnumerateArray(),
            candidate => candidate.GetProperty("snapshotId").GetString() == projectSnapshotId);
        var relativeManifestPath = snapshot.GetProperty("releaseManifestPath").GetString();
        var contentSha256 = snapshot.GetProperty("releaseContentSha256").GetString();
        Assert.False(string.IsNullOrWhiteSpace(relativeManifestPath));
        Assert.False(Path.IsPathRooted(relativeManifestPath));
        Assert.DoesNotContain("..", relativeManifestPath, StringComparison.Ordinal);
        Assert.NotNull(contentSha256);
        Assert.Equal(64, contentSha256.Length);
        Assert.All(contentSha256, character => Assert.True(Uri.IsHexDigit(character)));

        var absoluteManifestPath = Path.GetFullPath(Path.Combine(
            projectDirectory,
            relativeManifestPath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.StartsWith(
            Path.GetFullPath(projectDirectory) + Path.DirectorySeparatorChar,
            absoluteManifestPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(absoluteManifestPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(absoluteManifestPath));
        Assert.Equal("openlineops.project-release-artifact", manifest.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(contentSha256, manifest.RootElement.GetProperty("contentSha256").GetString());

        return new PublishedProjectRelease(relativeManifestPath, contentSha256, absoluteManifestPath);
    }

    private static async Task AssertProjectSnapshotRunsWithScopedConfigurationAsync(
        HttpClient client,
        string projectId,
        string projectSnapshotId,
        string applicationId,
        string topologyId,
        string productionLineDefinitionId,
        string processDefinitionId,
        string processVersionId,
        string configurationSnapshotId,
        string recipeVersionId)
    {
        var productionRunId = Guid.NewGuid();
        var productionUnitId = Guid.NewGuid();
        var identityValue = $"UNIT-{applicationId}";
        using var registerUnitResponse = await client.PostAsJsonAsync(
            "/api/production-units",
            new
            {
                productionUnitId,
                productModelId = "product.main",
                identityKey = "serialNumber",
                identityValue,
                lotId = (string?)null,
                occurredAtUtc = DateTimeOffset.UtcNow
            });
        Assert.Equal(HttpStatusCode.Created, registerUnitResponse.StatusCode);
        using var contextResponse = await client.GetAsync(
            $"/api/automation-projects/{Uri.EscapeDataString(projectId)}/snapshots/"
            + $"{Uri.EscapeDataString(projectSnapshotId)}/production-run-context");
        using var contextBody = await ReadJsonAsync(contextResponse);
        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        using var arriveUnitResponse = await client.PostAsJsonAsync(
            $"/api/production-units/{productionUnitId:D}/arrivals",
            new
            {
                projectId,
                applicationId,
                projectSnapshotId,
                packageContentSha256 = contextBody.RootElement
                    .GetProperty("entryStationPackageContentSha256")
                    .GetString(),
                stationId = contextBody.RootElement.GetProperty("entryStationId").GetString(),
                lineId = productionLineDefinitionId,
                stationSystemId = "station.main",
                occurredAtUtc = DateTimeOffset.UtcNow
            });
        Assert.Equal(HttpStatusCode.OK, arriveUnitResponse.StatusCode);

        using var startResponse = await client.PostAsJsonAsync(
            "/api/production-runs",
            new
            {
                projectId,
                projectSnapshotId,
                productionRunId = productionRunId.ToString("D"),
                productionUnitId = productionUnitId.ToString("D")
            });
        using var startBody = await ReadJsonAsync(startResponse);

        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
        Assert.Equal(projectSnapshotId, startBody.RootElement.GetProperty("projectSnapshotId").GetString());
        Assert.Equal(projectId, startBody.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(applicationId, startBody.RootElement.GetProperty("applicationId").GetString());
        Assert.Equal(topologyId, startBody.RootElement.GetProperty("topologyId").GetString());
        Assert.Equal(productionRunId, startBody.RootElement.GetProperty("productionRunId").GetGuid());
        Assert.Equal(
            ApiTestAuthentication.StandardActorId,
            startBody.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(identityValue, startBody.RootElement
            .GetProperty("productionUnitIdentity")
            .GetProperty("value")
            .GetString());
        Assert.Equal(JsonValueKind.Null, startBody.RootElement.GetProperty("lotId").ValueKind);
        Assert.Equal(JsonValueKind.Null, startBody.RootElement.GetProperty("carrierId").ValueKind);
        Assert.Equal("Pending", startBody.RootElement.GetProperty("executionStatus").GetString());

        using var completedRun = await WaitForTerminalProductionRunAsync(client, productionRunId);
        Assert.True(
            string.Equals(
                "Completed",
                completedRun.RootElement.GetProperty("executionStatus").GetString(),
                StringComparison.Ordinal),
            $"Production Run did not complete: {completedRun.RootElement.GetRawText()}");

        var operation = Assert.Single(completedRun.RootElement.GetProperty("operations").EnumerateArray());
        Assert.Equal("Completed", operation.GetProperty("executionStatus").GetString());
        Assert.Equal(processDefinitionId, operation.GetProperty("processDefinitionId").GetString());
        Assert.Equal(processVersionId, operation.GetProperty("processVersionId").GetString());
        Assert.Equal(configurationSnapshotId, operation.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal(recipeVersionId, operation.GetProperty("recipeSnapshotId").GetString());
        var sessionId = operation.GetProperty("runtimeSessionId").GetGuid();
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
        Assert.Equal("station.main", station.RootElement.GetProperty("stationSystemId").GetString());
        Assert.Equal($"{applicationName} Station", station.RootElement.GetProperty("displayName").GetString());
        var binding = Assert.Single(station.RootElement.GetProperty("deviceBindings").EnumerateArray());
        Assert.Equal("binding.primary", binding.GetProperty("deviceBindingId").GetString());
        Assert.Equal("station.main", binding.GetProperty("ownerSystemId").GetString());
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
        Assert.Equal("station.main", snapshotBinding.GetProperty("ownerSystemId").GetString());
        Assert.Equal(capabilityId, snapshotBinding.GetProperty("capabilityId").GetString());
        Assert.Equal(deviceKey, snapshotBinding.GetProperty("deviceKey").GetString());
    }

    private static bool IsMoveAxisBuiltInBlock(JsonElement block)
    {
        return block.GetProperty("blockType").GetString() == "openlineops_move_axis"
            && block.GetProperty("isBuiltIn").GetBoolean()
            && block.GetProperty("version").GetInt32() == 1;
    }

    private static JsonElement FindProcessNode(JsonDocument process, string nodeId)
    {
        return Assert.Single(
            process.RootElement.GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("nodeId").GetString() == nodeId);
    }

    private static JsonElement FindTopologySystem(JsonDocument topology, string systemId)
    {
        return Assert.Single(
            topology.RootElement.GetProperty("systems").EnumerateArray(),
            system => system.GetProperty("systemId").GetString() == systemId);
    }

    private static void AssertFixtureActionContract(JsonElement block)
    {
        var expected = FixtureActionContractArtifact();
        Assert.Equal(
            ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            block.GetProperty("executionMode").GetString());
        Assert.Equal(
            expected.SchemaVersion,
            block.GetProperty("runtimeActionContractSchemaVersion").GetString());
        Assert.Equal(
            expected.Sha256,
            block.GetProperty("runtimeActionContractSha256").GetString());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(expected.CanonicalJson),
            JsonNode.Parse(block.GetProperty("runtimeActionContract").GetRawText())));
    }

    private static RuntimeActionContractCanonicalArtifact FixtureActionContractArtifact()
    {
        var result = new RuntimeActionContractCanonicalSerializer().Serialize(
            new RuntimeActionContract(
                RuntimeActionContractSchema.Current,
                "fixture.wait",
                new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
                {
                    ["DURATION_MS"] = new RuntimeActionFieldDefinition(
                        RuntimeActionFieldType.WholeNumber,
                        Required: true,
                        Minimum: 0)
                },
                new RuntimeDelayEmit(new RuntimeActionFieldValue("DURATION_MS"))));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        return result.Value;
    }

    private sealed record PublishedProjectRelease(
        string RelativeManifestPath,
        string ContentSha256,
        string AbsoluteManifestPath);

    private sealed class ScriptWorkerWebApplicationFactory(string stationPackageDirectory) :
        StationPackageWebApplicationFactory(stationPackageDirectory)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            var repositoryRoot = FindRepositoryRoot();
            var workerProjectDirectory = Path.Combine(
                repositoryRoot,
                "src",
                "OpenLineOps.ScriptWorker");
            var testFrameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var buildConfiguration = testFrameworkDirectory.Parent?.Name
                ?? throw new InvalidOperationException(
                    "Could not resolve the test build configuration from the output directory.");
            var workerAssemblyPath = Path.Combine(
                workerProjectDirectory,
                "bin",
                buildConfiguration,
                testFrameworkDirectory.Name,
                "OpenLineOps.ScriptWorker.dll");
            if (!File.Exists(workerAssemblyPath))
            {
                throw new FileNotFoundException(
                    "The ScriptWorker must be built in the same configuration as the API tests.",
                    workerAssemblyPath);
            }

            builder.UseSetting(
                "OpenLineOps:Runtime:Scripting:Python:WorkerFileName",
                "dotnet");
            builder.UseSetting(
                "OpenLineOps:Runtime:Scripting:Python:WorkerArguments",
                $"\"{workerAssemblyPath}\"");
            builder.UseSetting(
                "OpenLineOps:Runtime:Scripting:Python:WorkerWorkingDirectory",
                repositoryRoot);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenLineOps.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the OpenLineOps repository root.");
        }
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
                    TargetKind: null,
                    TargetId: null,
                    TimeoutSeconds: null,
                    InputPayload: null,
                    BlocklyWorkspaceJson: null,
                    ScriptSourceCode: null,
                    ScriptVersion: null),
                new ApiCreateProcessNodeRequest(
                    "blockly",
                    "Blockly",
                    "Blockly Flow",
                    RequiredCapability: null,
                    CommandName: null,
                    TargetKind: null,
                    TargetId: null,
                    TimeoutSeconds: 10,
                    InputPayload: null,
                    BlocklyWorkspaceJson: blocklyWorkspaceJson,
                    ScriptSourceCode: null,
                    ScriptVersion: null),
                new ApiCreateProcessNodeRequest(
                    "script",
                    "PythonScript",
                    "Python Script",
                    RequiredCapability: null,
                    CommandName: null,
                    TargetKind: null,
                    TargetId: null,
                    TimeoutSeconds: 10,
                    InputPayload: null,
                    BlocklyWorkspaceJson: null,
                    ScriptSourceCode: sourceCode,
                    ScriptVersion: "1"),
                new ApiCreateProcessNodeRequest(
                    "end",
                    "End",
                    "End",
                    RequiredCapability: null,
                    CommandName: null,
                    TargetKind: null,
                    TargetId: null,
                    TimeoutSeconds: null,
                    InputPayload: null,
                    BlocklyWorkspaceJson: null,
                    ScriptSourceCode: null,
                    ScriptVersion: null)
            ],
            [
                new ApiCreateProcessTransitionRequest(
                    "start-to-blockly",
                    "start",
                    "blockly",
                    Label: null,
                    LoopPolicy: null,
                    MaxTraversals: null),
                new ApiCreateProcessTransitionRequest(
                    "blockly-to-script",
                    "blockly",
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

    private static async Task<JsonDocument> WaitForTerminalProductionRunAsync(
        HttpClient client,
        Guid productionRunId)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            using var response = await client.GetAsync($"/api/production-runs/{productionRunId:D}");
            var document = await ReadJsonAsync(response);
            if (response.StatusCode == HttpStatusCode.OK
                && document.RootElement.GetProperty("isTerminal").GetBoolean())
            {
                return document;
            }

            document.Dispose();
            await Task.Delay(50);
        }

        throw new TimeoutException($"Production Run {productionRunId:D} did not reach a terminal state.");
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
