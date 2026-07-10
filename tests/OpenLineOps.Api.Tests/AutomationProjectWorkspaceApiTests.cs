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
            var projectFilePath = createWorkspaceBody.RootElement.GetProperty("manifestPath").GetString();
            Assert.NotNull(projectFilePath);
            Assert.EndsWith(".oloproj", projectFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(projectFilePath));
            var applicationProjectRelativePath = createWorkspaceBody.RootElement
                .GetProperty("project")
                .GetProperty("applications")[0]
                .GetProperty("projectFilePath")
                .GetString();
            Assert.NotNull(applicationProjectRelativePath);
            Assert.EndsWith(".oloapp", applicationProjectRelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(
                projectDirectory,
                applicationProjectRelativePath.Replace('/', Path.DirectorySeparatorChar))));
            using (var projectFile = JsonDocument.Parse(await File.ReadAllTextAsync(projectFilePath)))
            {
                Assert.False(projectFile.RootElement.TryGetProperty("projectPath", out _));
            }

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
                    projectPath = projectFilePath
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
    public async Task ExistingApplicationProjectInsideWorkspaceCanBeImported()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-import-{suffix}";
        var applicationId = $"app-import-{suffix}";
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-api-application-import-tests",
            suffix);

        try
        {
            using var createResponse = await _client.PostAsJsonAsync(
                "/api/automation-project-workspaces",
                new
                {
                    projectId,
                    displayName = "Import Target",
                    projectPath = projectDirectory,
                    defaultApplicationId = (string?)null,
                    defaultApplicationName = (string?)null
                });
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

            var applicationDirectory = Path.Combine(
                projectDirectory,
                "applications",
                "copied-app");
            Directory.CreateDirectory(applicationDirectory);
            var applicationFilePath = Path.Combine(applicationDirectory, "copied.oloapp");
            await File.WriteAllTextAsync(
                applicationFilePath,
                $$"""
                {
                  "schemaVersion": "openlineops.automation-application/v1",
                  "formatVersion": 1,
                  "kind": "OpenLineOps.AutomationApplication",
                  "product": "OpenLineOps",
                  "applicationId": "{{applicationId}}",
                  "displayName": "Copied Application",
                  "resourceLayoutVersion": 1,
                  "topologyId": null,
                  "processDefinitionIds": []
                }
                """);

            using var importResponse = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/applications/import",
                new { projectFilePath = applicationFilePath });
            using var importBody = await ReadJsonAsync(importResponse);

            Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
            var application = importBody.RootElement
                .GetProperty("project")
                .GetProperty("applications")[0];
            Assert.Equal(applicationId, application.GetProperty("applicationId").GetString());
            Assert.Equal(
                "applications/copied-app/copied.oloapp",
                application.GetProperty("projectFilePath").GetString());
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
        var projectId = $"project-scoped-composition-{suffix}";
        var applicationId = $"application-scoped-composition-{suffix}";
        var topologyId = $"topology-scoped-composition-{suffix}";
        var layoutId = $"layout-scoped-composition-{suffix}";
        var processDefinitionId = $"process-scoped-composition-{suffix}";
        var processVersionId = $"{processDefinitionId}@1.0.0";
        var configurationSnapshotId = $"configuration-scoped-composition-{suffix}";
        var capabilityId = $"capability-scoped-composition-{suffix}";
        var bindingId = $"binding-scoped-composition-{suffix}";
        var snapshotId = $"snapshot-scoped-composition-{suffix}";
        var projectDirectory = ProjectReleaseTestDirectory(suffix);

        try
        {
            await CreateScopedReleaseSourceAsync(
                projectDirectory,
                projectId,
                applicationId,
                topologyId,
                layoutId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                capabilityId,
                bindingId,
                suffix);

            using var publishResponse = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/snapshots",
                new
                {
                    snapshotId,
                    applicationId,
                    processDefinitionId,
                    configurationSnapshotId
                });
            using var publishBody = await ReadJsonAsync(publishResponse);

            Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);
            Assert.Equal(snapshotId, publishBody.RootElement.GetProperty("activeSnapshotId").GetString());
            var snapshot = Assert.Single(publishBody.RootElement.GetProperty("snapshots").EnumerateArray());
            Assert.Equal(topologyId, snapshot.GetProperty("topologyId").GetString());
            Assert.Equal(layoutId, Assert.Single(snapshot.GetProperty("layoutIds").EnumerateArray()).GetString());
            Assert.Equal(processDefinitionId, snapshot.GetProperty("processDefinitionId").GetString());
            Assert.Equal(processVersionId, snapshot.GetProperty("processVersionId").GetString());
            Assert.Equal(configurationSnapshotId, snapshot.GetProperty("configurationSnapshotId").GetString());
            var binding = Assert.Single(snapshot.GetProperty("capabilityBindings").EnumerateArray());
            Assert.Equal(capabilityId, binding.GetProperty("capabilityId").GetString());
            Assert.Equal(bindingId, binding.GetProperty("bindingId").GetString());
            var targets = snapshot.GetProperty("targetReferences")
                .EnumerateArray()
                .Select(target => (
                    Kind: target.GetProperty("kind").GetString(),
                    TargetId: target.GetProperty("targetId").GetString()))
                .ToArray();
            Assert.Equal(3, targets.Length);
            Assert.Equal(targets.Length, targets.Distinct().Count());
            Assert.Equal(
                targets.OrderBy(target => target.Kind, StringComparer.Ordinal)
                    .ThenBy(target => target.TargetId, StringComparer.Ordinal)
                    .ToArray(),
                targets);
            Assert.Contains(targets, target => target is { Kind: "System", TargetId: "station.main" });
            Assert.Contains(targets, target => target is { Kind: "Capability" }
                && target.TargetId == capabilityId);
            Assert.Contains(targets, target => target is { Kind: "Driver" }
                && target.TargetId == bindingId);
            Assert.Empty(snapshot.GetProperty("blockVersionIds").EnumerateArray());
            AssertImmutableRelease(snapshot, projectDirectory);

            using var queryProjectResponse = await _client.GetAsync($"/api/automation-projects/{projectId}");
            using var queryProjectBody = await ReadJsonAsync(queryProjectResponse);
            Assert.Equal(HttpStatusCode.OK, queryProjectResponse.StatusCode);
            Assert.Equal(snapshotId, queryProjectBody.RootElement.GetProperty("activeSnapshotId").GetString());
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
    }

    [Fact]
    public async Task PublishedProjectSnapshotCanStartRuntimeSession()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-scoped-runtime-{suffix}";
        var applicationId = $"application-scoped-runtime-{suffix}";
        var topologyId = $"topology-scoped-runtime-{suffix}";
        var layoutId = $"layout-scoped-runtime-{suffix}";
        var processDefinitionId = $"process-scoped-runtime-{suffix}";
        const string processVersionId = "packaging-line-eol@1.0.0";
        var configurationSnapshotId = $"configuration-scoped-runtime-{suffix}";
        const string capabilityId = "device.scanner";
        var bindingId = $"binding-scoped-runtime-{suffix}";
        var snapshotId = $"snapshot-scoped-runtime-{suffix}";
        var projectDirectory = ProjectReleaseTestDirectory(suffix);

        try
        {
            await CreateScopedReleaseSourceAsync(
                projectDirectory,
                projectId,
                applicationId,
                topologyId,
                layoutId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                capabilityId,
                bindingId,
                suffix);

            using var publishResponse = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/snapshots",
                new
                {
                    snapshotId,
                    applicationId,
                    processDefinitionId,
                    configurationSnapshotId
                });
            using var publishBody = await ReadJsonAsync(publishResponse);
            Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);
            var snapshot = Assert.Single(publishBody.RootElement.GetProperty("snapshots").EnumerateArray());
            AssertImmutableRelease(snapshot, projectDirectory);

            var liveFlowPath = Assert.Single(Directory.GetFiles(
                Path.Combine(projectDirectory, "applications"),
                "flow.json",
                SearchOption.AllDirectories));
            File.Delete(liveFlowPath);

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
            Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
            var sessionId = startBody.RootElement.GetProperty("sessionId").GetGuid();
            Assert.Equal(snapshotId, startBody.RootElement.GetProperty("snapshotId").GetString());
            Assert.Equal(projectId, startBody.RootElement.GetProperty("projectId").GetString());
            Assert.Equal(applicationId, startBody.RootElement.GetProperty("applicationId").GetString());
            Assert.Equal(topologyId, startBody.RootElement.GetProperty("topologyId").GetString());
            Assert.Equal(configurationSnapshotId, startBody.RootElement.GetProperty("configurationSnapshotId").GetString());
            Assert.Equal("Completed", startBody.RootElement.GetProperty("status").GetString());

            using var sessionResponse = await _client.GetAsync($"/api/runtime/sessions/{sessionId}");
            using var sessionBody = await ReadJsonAsync(sessionResponse);
            Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
            Assert.Equal(processDefinitionId, sessionBody.RootElement.GetProperty("processDefinitionId").GetString());
            Assert.Equal(processVersionId, sessionBody.RootElement.GetProperty("processVersionId").GetString());
            Assert.Equal(configurationSnapshotId, sessionBody.RootElement.GetProperty("configurationSnapshotId").GetString());
            Assert.Equal(snapshotId, sessionBody.RootElement.GetProperty("projectSnapshotId").GetString());
            Assert.Equal($"SN-{suffix}", sessionBody.RootElement.GetProperty("serialNumber").GetString());

            using var traceResponse = await _client.GetAsync(
                $"/api/traceability/read-models/engineering-search?projectSnapshotId={Uri.EscapeDataString(snapshotId)}");
            using var traceBody = await ReadJsonAsync(traceResponse);
            Assert.Equal(HttpStatusCode.OK, traceResponse.StatusCode);
            var traceRow = Assert.Single(traceBody.RootElement.GetProperty("results").GetProperty("items").EnumerateArray());
            Assert.Equal(sessionId, traceRow.GetProperty("runtimeSessionId").GetGuid());
            Assert.Equal(snapshotId, traceRow.GetProperty("projectSnapshotId").GetString());
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
    }

    [Fact]
    public async Task SiteLayoutRejectsMissingTopologyTarget()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-layout-missing-target-{suffix}";
        var applicationId = $"application-layout-missing-target-{suffix}";
        var topologyId = $"topology-layout-missing-target-{suffix}";
        var layoutId = $"layout-missing-target-{suffix}";
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-api-layout-validation-tests",
            suffix);

        try
        {
            using var createWorkspaceResponse = await _client.PostAsJsonAsync(
                "/api/automation-project-workspaces",
                new
                {
                    projectId,
                    displayName = "Layout Validation Project",
                    projectPath = projectDirectory,
                    defaultApplicationId = applicationId,
                    defaultApplicationName = "Layout Validation Application"
                });
            Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);

            var topologiesPath = ProjectTopologiesPath(projectId, applicationId);
            using var createTopologyResponse = await _client.PostAsJsonAsync(
                topologiesPath,
                new
                {
                    topologyId,
                    displayName = "Layout Validation Topology"
                });
            Assert.Equal(HttpStatusCode.Created, createTopologyResponse.StatusCode);

            var layoutsPath = ProjectLayoutsPath(projectId, applicationId);
            using var createLayoutResponse = await _client.PostAsJsonAsync(
                layoutsPath,
                new
                {
                    layoutId,
                    topologyId,
                    displayName = "Layout",
                    canvasWidth = 800,
                    canvasHeight = 600,
                    units = "mm"
                });
            Assert.Equal(HttpStatusCode.Created, createLayoutResponse.StatusCode);

            using var addMissingTargetResponse = await _client.PostAsJsonAsync(
                $"{layoutsPath}/{layoutId}/elements",
                new
                {
                    elementId = $"layout-missing-system-{suffix}",
                    kind = "SystemShape",
                    target = new
                    {
                        kind = "System",
                        targetId = $"missing-system-{suffix}"
                    },
                    parentElementId = (string?)null,
                    x = 10,
                    y = 20,
                    width = 160,
                    height = 120,
                    rotationDegrees = 0,
                    zIndex = 1,
                    style = new Dictionary<string, string>()
                });
            using var body = await ReadJsonAsync(addMissingTargetResponse);

            Assert.Equal(HttpStatusCode.BadRequest, addMissingTargetResponse.StatusCode);
            Assert.Equal(
                "Validation.Topology.LayoutTargetMissing",
                body.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
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

    private async Task CreateScopedReleaseSourceAsync(
        string projectDirectory,
        string projectId,
        string applicationId,
        string topologyId,
        string layoutId,
        string processDefinitionId,
        string processVersionId,
        string configurationSnapshotId,
        string capabilityId,
        string bindingId,
        string suffix)
    {
        using var createWorkspaceResponse = await _client.PostAsJsonAsync(
            "/api/automation-project-workspaces",
            new
            {
                projectId,
                displayName = $"Scoped Release {suffix}",
                projectPath = projectDirectory,
                defaultApplicationId = applicationId,
                defaultApplicationName = "Main Application"
            });
        Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);

        var topologiesPath = ProjectTopologiesPath(projectId, applicationId);
        using var createTopologyResponse = await _client.PostAsJsonAsync(
            topologiesPath,
            new { topologyId, displayName = "Scoped Release Topology" });
        Assert.Equal(HttpStatusCode.Created, createTopologyResponse.StatusCode);
        using var capabilityResponse = await _client.PostAsJsonAsync(
            $"{topologiesPath}/{topologyId}/capabilities",
            new
            {
                capabilityId,
                commandName = "Inspect",
                version = "1.0.0",
                inputSchema = """{"type":"object"}""",
                outputSchema = (string?)null,
                timeoutSeconds = 30,
                safetyClass = "Normal"
            });
        Assert.Equal(HttpStatusCode.OK, capabilityResponse.StatusCode);
        await AddProjectNodeAsync(
            topologiesPath, topologyId, "station.main", null, "Station", "Main Station");
        using var bindingResponse = await _client.PostAsJsonAsync(
            $"{topologiesPath}/{topologyId}/driver-bindings",
            new
            {
                bindingId,
                capabilityId,
                providerKind = "Simulator",
                providerKey = $"simulator.{applicationId}"
            });
        Assert.Equal(HttpStatusCode.OK, bindingResponse.StatusCode);

        var layoutsPath = ProjectLayoutsPath(projectId, applicationId);
        using var createLayoutResponse = await _client.PostAsJsonAsync(
            layoutsPath,
            new
            {
                layoutId,
                topologyId,
                displayName = "Scoped Release Layout",
                canvasWidth = 800,
                canvasHeight = 600,
                units = "mm"
            });
        Assert.Equal(HttpStatusCode.Created, createLayoutResponse.StatusCode);
        using var addLayoutElementResponse = await _client.PostAsJsonAsync(
            $"{layoutsPath}/{layoutId}/elements",
            new
            {
                elementId = "element.station",
                kind = "SystemShape",
                target = new { kind = "System", targetId = "station.main" },
                parentElementId = (string?)null,
                x = 20,
                y = 30,
                width = 100,
                height = 80,
                rotationDegrees = 0,
                zIndex = 1,
                style = new Dictionary<string, string>()
            });
        Assert.Equal(HttpStatusCode.OK, addLayoutElementResponse.StatusCode);

        var processesPath = ProjectProcessesPath(projectId, applicationId);
        using var createProcessResponse = await _client.PostAsJsonAsync(
            processesPath,
            CreateRuntimeProcessDefinitionRequest(processDefinitionId, processVersionId, capabilityId));
        Assert.Equal(HttpStatusCode.Created, createProcessResponse.StatusCode);
        using var publishProcessResponse = await _client.PostAsync(
            $"{processesPath}/{processDefinitionId}/publish",
            content: null);
        Assert.Equal(HttpStatusCode.OK, publishProcessResponse.StatusCode);

        await CreateScopedPublishedEngineeringSnapshotAsync(
            projectId,
            applicationId,
            $"recipe-{suffix}",
            $"station-{suffix}",
            $"engineering-{suffix}",
            configurationSnapshotId,
            processDefinitionId,
            processVersionId,
            capabilityId);

        using var linkTopologyResponse = await _client.PutAsJsonAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/topology",
            new { topologyId });
        Assert.Equal(HttpStatusCode.OK, linkTopologyResponse.StatusCode);
        using var linkProcessResponse = await _client.PutAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/process-definitions/{processDefinitionId}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, linkProcessResponse.StatusCode);
    }

    private async Task CreateScopedPublishedEngineeringSnapshotAsync(
        string projectId,
        string applicationId,
        string recipeId,
        string stationProfileId,
        string engineeringProjectId,
        string configurationSnapshotId,
        string processDefinitionId,
        string processVersionId,
        string capabilityId)
    {
        var engineeringBase = $"/api/automation-projects/{projectId}/applications/{applicationId}/engineering";
        var workspaceId = $"workspace-{engineeringProjectId}";
        using var createWorkspaceResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/workspaces",
            new { workspaceId, displayName = "Release Workspace" });
        using var createRecipeResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/recipes",
            new
            {
                recipeId,
                versionId = $"{recipeId}@1.0.0",
                displayName = "Release Recipe",
                parameters = new[] { new { key = "scan.mode", value = "release" } }
            });
        using var publishRecipeResponse = await _client.PostAsync(
            $"{engineeringBase}/recipes/{recipeId}/publish",
            content: null);
        using var createStationResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/station-profiles",
            new
            {
                stationProfileId,
                stationSystemId = "station.main",
                displayName = "Release Station",
                deviceBindings = new[]
                {
                    new
                    {
                        deviceBindingId = "binding.primary",
                        capabilityId,
                        deviceKey = "scanner-01"
                    }
                }
            });
        using var createProjectResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/projects",
            new
            {
                projectId = engineeringProjectId,
                workspaceId,
                displayName = "Release Engineering Project"
            });
        using var publishSnapshotResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/projects/{engineeringProjectId}/configuration-snapshots",
            new
            {
                snapshotId = configurationSnapshotId,
                processDefinitionId,
                processVersionId,
                recipeId,
                stationProfileId
            });

        Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createStationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createProjectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, publishSnapshotResponse.StatusCode);
    }

    private async Task AddProjectNodeAsync(
        string topologiesPath,
        string topologyId,
        string nodeId,
        string? parentNodeId,
        string kind,
        string displayName)
    {
        using var response = await _client.PostAsJsonAsync(
            $"{topologiesPath}/{topologyId}/systems",
            new
            {
                systemId = nodeId,
                parentSystemId = parentNodeId,
                kind,
                systemType = "test.system",
                displayName,
                requiredCapabilityIds = Array.Empty<string>(),
                providedCapabilityIds = Array.Empty<string>(),
                metadata = new Dictionary<string, string>()
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void AssertImmutableRelease(JsonElement snapshot, string projectDirectory)
    {
        var relativeManifestPath = snapshot.GetProperty("releaseManifestPath").GetString();
        var contentSha256 = snapshot.GetProperty("releaseContentSha256").GetString();
        Assert.False(string.IsNullOrWhiteSpace(relativeManifestPath));
        Assert.False(Path.IsPathRooted(relativeManifestPath));
        Assert.DoesNotContain("..", relativeManifestPath, StringComparison.Ordinal);
        Assert.NotNull(contentSha256);
        Assert.Equal(64, contentSha256.Length);
        Assert.All(contentSha256, character => Assert.True(Uri.IsHexDigit(character)));

        var manifestPath = Path.GetFullPath(Path.Combine(
            projectDirectory,
            relativeManifestPath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.StartsWith(
            Path.GetFullPath(projectDirectory) + Path.DirectorySeparatorChar,
            manifestPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(manifestPath), $"Release manifest was not found at {manifestPath}.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal("openlineops.project-release-artifact", manifest.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(contentSha256, manifest.RootElement.GetProperty("contentSha256").GetString());
    }

    private static string ProjectReleaseTestDirectory(string suffix)
    {
        return Path.Combine(Path.GetTempPath(), "openlineops-api-project-release-tests", suffix);
    }

    private static void DeleteProjectDirectory(string projectDirectory)
    {
        if (Directory.Exists(projectDirectory))
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    private static string ProjectTopologiesPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/topologies";
    }

    private static string ProjectLayoutsPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/layouts";
    }

    private static string ProjectProcessesPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/processes";
    }

    private static object CreateRuntimeProcessDefinitionRequest(
        string processDefinitionId,
        string processVersionId = "packaging-line-eol@1.0.0",
        string capabilityId = "device.scanner")
    {
        return new
        {
            processDefinitionId,
            versionId = processVersionId,
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
                    requiredCapability = (string?)capabilityId,
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
