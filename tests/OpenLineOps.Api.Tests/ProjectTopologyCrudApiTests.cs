using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class ProjectTopologyCrudApiTests : IDisposable
{
    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-topology-crud-api-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScopedCrudIsStrictCascadesLayoutAndSurvivesHostRestart()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-{suffix}";
        var applicationId = $"app-{suffix}";
        const string topologyId = "Topology.Main";
        const string layoutId = "Layout.Main";
        var topologyPath = TopologyPath(projectId, applicationId, topologyId);
        var layoutPath = LayoutPath(projectId, applicationId, layoutId);

        using (var factory = new WebApplicationFactory<Program>())
        using (var client = factory.CreateClient())
        {
            using var createWorkspace = await client.PostAsJsonAsync(
                "/api/automation-project-workspaces",
                new
                {
                    projectId,
                    displayName = "Topology CRUD",
                    projectPath = _projectDirectory,
                    defaultApplicationId = applicationId,
                    defaultApplicationName = "Main Application"
                });
            Assert.Equal(HttpStatusCode.Created, createWorkspace.StatusCode);

            using var createTopology = await client.PostAsJsonAsync(
                TopologiesPath(projectId, applicationId),
                new { topologyId, displayName = "Main Topology" });
            Assert.Equal(HttpStatusCode.Created, createTopology.StatusCode);
            await AddSystemAsync(client, topologyPath, "Station.Main", null, "Station", "Primary Station");
            await AddSystemAsync(client, topologyPath, "System.Child", "Station.Main", "System", "Disposable Child");
            using var addGroup = await client.PostAsJsonAsync(
                $"{topologyPath}/slot-groups",
                new
                {
                    slotGroupId = "Group.Main",
                    parentSystemId = "Station.Main",
                    displayName = "Fixture",
                    kind = "FixtureNest",
                    capacity = 2
                });
            Assert.Equal(HttpStatusCode.OK, addGroup.StatusCode);
            await AddSlotAsync(client, topologyPath, "Slot.One", "A1", "Position One");
            await AddSlotAsync(client, topologyPath, "Slot.Two", "B1", "Position Two");

            using var createLayout = await client.PostAsJsonAsync(
                LayoutsPath(projectId, applicationId),
                new
                {
                    layoutId,
                    topologyId,
                    displayName = "Main Layout",
                    canvasWidth = 1200,
                    canvasHeight = 680,
                    units = "px"
                });
            Assert.Equal(HttpStatusCode.Created, createLayout.StatusCode);
            await AddElementAsync(client, layoutPath, "station.shape", "SystemShape", "System", "Station.Main", null, 40, 40, 400, 290);
            await AddElementAsync(client, layoutPath, "child.shape", "SystemShape", "System", "System.Child", "station.shape", 20, 58, 150, 56);
            await AddElementAsync(client, layoutPath, "group.region", "GroupRegion", "SlotGroup", "Group.Main", "station.shape", 20, 140, 180, 120);
            await AddElementAsync(client, layoutPath, "slot.one.shape", "SlotShape", "Slot", "Slot.One", "group.region", 10, 42, 28, 26);
            await AddElementAsync(client, layoutPath, "slot.two.shape", "SlotShape", "Slot", "Slot.Two", "group.region", 48, 42, 28, 26);

            using var unknownField = await client.PatchAsJsonAsync(
                $"{topologyPath}/systems/System.Child",
                new { displayName = "Rejected", unsupported = true });
            Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
            using var rename = await client.PatchAsJsonAsync(
                $"{topologyPath}/systems/System.Child",
                new
                {
                    displayName = "Vision Controller",
                    systemType = "vision.controller",
                    metadata = new Dictionary<string, string> { ["vendor"] = "acme" }
                });
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
            using var renameBody = await ReadJsonAsync(rename);
            var renamedSystem = Assert.Single(
                renameBody.RootElement.GetProperty("systems").EnumerateArray(),
                system => system.GetProperty("systemId").GetString() == "System.Child");
            Assert.Equal("Vision Controller", renamedSystem.GetProperty("displayName").GetString());

            using var capacityBelowCount = await client.PatchAsJsonAsync(
                $"{topologyPath}/slot-groups/Group.Main",
                new { capacity = 1 });
            Assert.Equal(HttpStatusCode.Conflict, capacityBelowCount.StatusCode);
            using var caseDistinctAddress = await client.PatchAsJsonAsync(
                $"{topologyPath}/slots/Slot.Two",
                new { address = "a1" });
            Assert.Equal(HttpStatusCode.OK, caseDistinctAddress.StatusCode);
            using var duplicateAddress = await client.PatchAsJsonAsync(
                $"{topologyPath}/slots/Slot.Two",
                new { address = "A1" });
            Assert.Equal(HttpStatusCode.Conflict, duplicateAddress.StatusCode);

            using var wrongCaseDelete = await client.DeleteAsync($"{topologyPath}/systems/system.child");
            Assert.Equal(HttpStatusCode.NotFound, wrongCaseDelete.StatusCode);
            using var deleteChild = await client.DeleteAsync($"{topologyPath}/systems/System.Child");
            using var deleteChildBody = await ReadJsonAsync(deleteChild);
            Assert.Equal(HttpStatusCode.OK, deleteChild.StatusCode);
            Assert.Equal(1, deleteChildBody.RootElement.GetProperty("updatedLayoutCount").GetInt32());
            Assert.Equal(1, deleteChildBody.RootElement.GetProperty("removedLayoutElementCount").GetInt32());
            Assert.Contains(
                "Production",
                deleteChildBody.RootElement.GetProperty("publicationImpact").GetString(),
                StringComparison.Ordinal);

            using var deleteGroup = await client.DeleteAsync($"{topologyPath}/slot-groups/Group.Main");
            using var deleteGroupBody = await ReadJsonAsync(deleteGroup);
            Assert.Equal(HttpStatusCode.OK, deleteGroup.StatusCode);
            Assert.Equal(3, deleteGroupBody.RootElement.GetProperty("removedLayoutElementCount").GetInt32());
            Assert.Empty(deleteGroupBody.RootElement.GetProperty("topology").GetProperty("slotGroups").EnumerateArray());
            Assert.Empty(deleteGroupBody.RootElement.GetProperty("topology").GetProperty("slots").EnumerateArray());

            using var getLayout = await client.GetAsync(layoutPath);
            using var layout = await ReadJsonAsync(getLayout);
            var onlyElement = Assert.Single(layout.RootElement.GetProperty("elements").EnumerateArray());
            Assert.Equal("Station.Main", onlyElement.GetProperty("target").GetProperty("targetId").GetString());
        }

        using (var restartedFactory = new WebApplicationFactory<Program>())
        using (var restartedClient = restartedFactory.CreateClient())
        {
            using var reopen = await restartedClient.PostAsJsonAsync(
                "/api/automation-project-workspaces/open",
                new { projectPath = _projectDirectory });
            Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
            using var topologyResponse = await restartedClient.GetAsync(topologyPath);
            using var topology = await ReadJsonAsync(topologyResponse);
            Assert.Equal(HttpStatusCode.OK, topologyResponse.StatusCode);
            var station = Assert.Single(topology.RootElement.GetProperty("systems").EnumerateArray());
            Assert.Equal("Station.Main", station.GetProperty("systemId").GetString());
            Assert.Empty(topology.RootElement.GetProperty("slotGroups").EnumerateArray());
            Assert.Empty(topology.RootElement.GetProperty("slots").EnumerateArray());

            using var layoutResponse = await restartedClient.GetAsync(layoutPath);
            using var layout = await ReadJsonAsync(layoutResponse);
            Assert.Equal(HttpStatusCode.OK, layoutResponse.StatusCode);
            Assert.Single(layout.RootElement.GetProperty("elements").EnumerateArray());
        }
    }

    private static async Task AddSystemAsync(
        HttpClient client,
        string topologyPath,
        string systemId,
        string? parentSystemId,
        string kind,
        string displayName)
    {
        using var response = await client.PostAsJsonAsync(
            $"{topologyPath}/systems",
            new
            {
                systemId,
                parentSystemId,
                kind,
                systemType = $"test.{kind.ToLowerInvariant()}",
                displayName,
                requiredCapabilityIds = Array.Empty<string>(),
                providedCapabilityIds = Array.Empty<string>(),
                metadata = new Dictionary<string, string>()
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AddSlotAsync(
        HttpClient client,
        string topologyPath,
        string slotId,
        string address,
        string displayName)
    {
        using var response = await client.PostAsJsonAsync(
            $"{topologyPath}/slots",
            new
            {
                slotGroupId = "Group.Main",
                slotId,
                parentSystemId = "Station.Main",
                address,
                displayName,
                materialKind = "Dut",
                isEnabled = true
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AddElementAsync(
        HttpClient client,
        string layoutPath,
        string elementId,
        string kind,
        string targetKind,
        string targetId,
        string? parentElementId,
        double x,
        double y,
        double width,
        double height)
    {
        using var response = await client.PostAsJsonAsync(
            $"{layoutPath}/elements",
            new
            {
                elementId,
                kind,
                target = new { kind = targetKind, targetId },
                parentElementId,
                x,
                y,
                width,
                height,
                rotationDegrees = 0,
                zIndex = 1,
                style = new Dictionary<string, string>()
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static string TopologiesPath(string projectId, string applicationId) =>
        $"/api/automation-projects/{projectId}/applications/{applicationId}/topologies";

    private static string TopologyPath(string projectId, string applicationId, string topologyId) =>
        $"{TopologiesPath(projectId, applicationId)}/{topologyId}";

    private static string LayoutsPath(string projectId, string applicationId) =>
        $"/api/automation-projects/{projectId}/applications/{applicationId}/layouts";

    private static string LayoutPath(string projectId, string applicationId, string layoutId) =>
        $"{LayoutsPath(projectId, applicationId)}/{layoutId}";

    public void Dispose()
    {
        if (Directory.Exists(_projectDirectory))
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
    }
}
