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
            Assert.Equal("Draft", processB.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                ReplacementPythonSource,
                processB.RootElement.GetProperty("nodes")[1].GetProperty("scriptSourceCode").GetString());
            Assert.Equal(
                ReplacementBlocklyWorkspaceJson,
                processB.RootElement.GetProperty("nodes")[1].GetProperty("blocklyWorkspaceJson").GetString());

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

    private string MovedProjectDirectory => $"{_projectDirectory}-moved";
}
