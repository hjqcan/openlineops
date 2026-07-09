using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class EngineeringConfigurationApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public EngineeringConfigurationApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task PublishedEngineeringInputsCanCreateSnapshotAndRollback()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workspaceId = $"workspace-api-{suffix}";
        var recipeId = $"recipe-api-{suffix}";
        var stationProfileId = $"station-api-{suffix}";
        var projectId = $"project-api-{suffix}";
        var firstSnapshotId = $"snapshot-api-first-{suffix}";
        var secondSnapshotId = $"snapshot-api-second-{suffix}";

        using var createWorkspaceResponse = await _client.PostAsJsonAsync(
            "/api/engineering/workspaces",
            CreateWorkspaceRequest(workspaceId));
        using var createRecipeResponse = await _client.PostAsJsonAsync(
            "/api/engineering/recipes",
            CreateRecipeRequest(recipeId));
        using var createRecipeBody = await ReadJsonAsync(createRecipeResponse);

        Assert.Equal(HttpStatusCode.Created, createRecipeResponse.StatusCode);
        Assert.Equal("Draft", createRecipeBody.RootElement.GetProperty("status").GetString());

        using var publishRecipeResponse = await _client.PostAsync(
            $"/api/engineering/recipes/{recipeId}/publish",
            content: null);
        using var publishRecipeBody = await ReadJsonAsync(publishRecipeResponse);

        Assert.Equal(HttpStatusCode.OK, publishRecipeResponse.StatusCode);
        Assert.Equal("Published", publishRecipeBody.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, publishRecipeBody.RootElement.GetProperty("publishedAtUtc").ValueKind);

        using var createStationResponse = await _client.PostAsJsonAsync(
            "/api/engineering/station-profiles",
            CreateStationProfileRequest(stationProfileId));
        using var createStationBody = await ReadJsonAsync(createStationResponse);

        Assert.Equal(HttpStatusCode.Created, createStationResponse.StatusCode);
        Assert.Equal(stationProfileId, createStationBody.RootElement.GetProperty("stationProfileId").GetString());
        Assert.Single(createStationBody.RootElement.GetProperty("deviceBindings").EnumerateArray());

        using var createProjectResponse = await _client.PostAsJsonAsync(
            "/api/engineering/projects",
            CreateProjectRequest(projectId, workspaceId));
        using var createProjectBody = await ReadJsonAsync(createProjectResponse);

        Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createProjectResponse.StatusCode);
        Assert.Equal(projectId, createProjectBody.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(workspaceId, createProjectBody.RootElement.GetProperty("workspaceId").GetString());
        Assert.Equal(JsonValueKind.Null, createProjectBody.RootElement.GetProperty("activeSnapshotId").ValueKind);

        using var firstSnapshotResponse = await _client.PostAsJsonAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots",
            PublishSnapshotRequest(firstSnapshotId, recipeId, stationProfileId));
        using var firstSnapshotBody = await ReadJsonAsync(firstSnapshotResponse);

        Assert.Equal(HttpStatusCode.Created, firstSnapshotResponse.StatusCode);
        Assert.Equal(firstSnapshotId, firstSnapshotBody.RootElement.GetProperty("activeSnapshotId").GetString());
        AssertSnapshot(firstSnapshotBody, firstSnapshotId, recipeId, stationProfileId);

        using var secondSnapshotResponse = await _client.PostAsJsonAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots",
            PublishSnapshotRequest(secondSnapshotId, recipeId, stationProfileId));
        using var secondSnapshotBody = await ReadJsonAsync(secondSnapshotResponse);

        Assert.Equal(HttpStatusCode.Created, secondSnapshotResponse.StatusCode);
        Assert.Equal(secondSnapshotId, secondSnapshotBody.RootElement.GetProperty("activeSnapshotId").GetString());
        Assert.Equal(2, secondSnapshotBody.RootElement.GetProperty("snapshots").GetArrayLength());

        using var rollbackResponse = await _client.PostAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots/{firstSnapshotId}/rollback",
            content: null);
        using var rollbackBody = await ReadJsonAsync(rollbackResponse);

        Assert.Equal(HttpStatusCode.OK, rollbackResponse.StatusCode);
        Assert.Equal(firstSnapshotId, rollbackBody.RootElement.GetProperty("activeSnapshotId").GetString());
    }

    [Fact]
    public async Task DraftRecipeCannotCreateConfigurationSnapshot()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workspaceId = $"workspace-api-draft-{suffix}";
        var recipeId = $"recipe-api-draft-{suffix}";
        var stationProfileId = $"station-api-draft-{suffix}";
        var projectId = $"project-api-draft-{suffix}";
        var snapshotId = $"snapshot-api-draft-{suffix}";

        using var createWorkspaceResponse = await _client.PostAsJsonAsync(
            "/api/engineering/workspaces",
            CreateWorkspaceRequest(workspaceId));
        using var createRecipeResponse = await _client.PostAsJsonAsync(
            "/api/engineering/recipes",
            CreateRecipeRequest(recipeId));
        using var createStationResponse = await _client.PostAsJsonAsync(
            "/api/engineering/station-profiles",
            CreateStationProfileRequest(stationProfileId));
        using var createProjectResponse = await _client.PostAsJsonAsync(
            "/api/engineering/projects",
            CreateProjectRequest(projectId, workspaceId));
        using var snapshotResponse = await _client.PostAsJsonAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots",
            PublishSnapshotRequest(snapshotId, recipeId, stationProfileId));

        Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createStationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createProjectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, snapshotResponse.StatusCode);
    }

    [Fact]
    public async Task DuplicateStationCapabilityBindingReturnsBadRequest()
    {
        var stationProfileId = $"station-api-duplicate-{Guid.NewGuid():N}";

        using var response = await _client.PostAsJsonAsync(
            "/api/engineering/station-profiles",
            new
            {
                stationProfileId,
                displayName = "Duplicate Capability Station",
                deviceBindings = new[]
                {
                    new
                    {
                        deviceBindingId = "scanner-primary",
                        capabilityId = "device.scanner",
                        deviceKey = "scanner-01"
                    },
                    new
                    {
                        deviceBindingId = "scanner-secondary",
                        capabilityId = "device.scanner",
                        deviceKey = "scanner-02"
                    }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WorkspaceLifecycleCanCreateListAndQuery()
    {
        var workspaceId = $"workspace-api-lifecycle-{Guid.NewGuid():N}";

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/engineering/workspaces",
            CreateWorkspaceRequest(workspaceId));
        using var createBody = await ReadJsonAsync(createResponse);

        using var listResponse = await _client.GetAsync("/api/engineering/workspaces");
        using var listBody = await ReadJsonAsync(listResponse);

        using var getResponse = await _client.GetAsync($"/api/engineering/workspaces/{workspaceId}");
        using var getBody = await ReadJsonAsync(getResponse);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Contains($"/api/engineering/workspaces/{workspaceId}", createResponse.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(workspaceId, createBody.RootElement.GetProperty("workspaceId").GetString());
        Assert.Equal("Main Workspace", createBody.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains(
            listBody.RootElement.EnumerateArray(),
            workspace => workspace.GetProperty("workspaceId").GetString() == workspaceId);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(workspaceId, getBody.RootElement.GetProperty("workspaceId").GetString());
    }

    [Fact]
    public async Task ProjectCreationRequiresExistingWorkspace()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-api-missing-workspace-{suffix}";
        var workspaceId = $"workspace-api-missing-{suffix}";

        using var response = await _client.PostAsJsonAsync(
            "/api/engineering/projects",
            CreateProjectRequest(projectId, workspaceId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConfigurationSnapshotDiffReturnsRecipeStationAndDeviceChanges()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workspaceId = $"workspace-api-diff-{suffix}";
        var firstRecipeId = $"recipe-api-diff-first-{suffix}";
        var secondRecipeId = $"recipe-api-diff-second-{suffix}";
        var firstStationProfileId = $"station-api-diff-first-{suffix}";
        var secondStationProfileId = $"station-api-diff-second-{suffix}";
        var projectId = $"project-api-diff-{suffix}";
        var firstSnapshotId = $"snapshot-api-diff-first-{suffix}";
        var secondSnapshotId = $"snapshot-api-diff-second-{suffix}";

        using var firstRecipeResponse = await _client.PostAsJsonAsync(
            "/api/engineering/recipes",
            CreateRecipeRequest(firstRecipeId));
        using var firstRecipePublishResponse = await _client.PostAsync(
            $"/api/engineering/recipes/{firstRecipeId}/publish",
            content: null);
        using var secondRecipeResponse = await _client.PostAsJsonAsync(
            "/api/engineering/recipes",
            CreateRecipeRequest(secondRecipeId));
        using var secondRecipePublishResponse = await _client.PostAsync(
            $"/api/engineering/recipes/{secondRecipeId}/publish",
            content: null);
        using var firstStationResponse = await _client.PostAsJsonAsync(
            "/api/engineering/station-profiles",
            CreateStationProfileRequest(firstStationProfileId));
        using var secondStationResponse = await _client.PostAsJsonAsync(
            "/api/engineering/station-profiles",
            CreateStationProfileRequest(
                secondStationProfileId,
                "multimeter-primary",
                "device.multimeter",
                "meter-01"));
        using var workspaceResponse = await _client.PostAsJsonAsync(
            "/api/engineering/workspaces",
            CreateWorkspaceRequest(workspaceId));
        using var projectResponse = await _client.PostAsJsonAsync(
            "/api/engineering/projects",
            CreateProjectRequest(projectId, workspaceId));
        using var firstSnapshotResponse = await _client.PostAsJsonAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots",
            PublishSnapshotRequest(firstSnapshotId, firstRecipeId, firstStationProfileId));
        using var secondSnapshotResponse = await _client.PostAsJsonAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots",
            PublishSnapshotRequest(secondSnapshotId, secondRecipeId, secondStationProfileId));

        using var diffResponse = await _client.GetAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots/{firstSnapshotId}/diff/{secondSnapshotId}");
        using var diffBody = await ReadJsonAsync(diffResponse);
        var changes = diffBody.RootElement.GetProperty("changes").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.Created, firstRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstRecipePublishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondRecipePublishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, firstStationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondStationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, workspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, firstSnapshotResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondSnapshotResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, diffResponse.StatusCode);
        Assert.Equal(projectId, diffBody.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(firstSnapshotId, diffBody.RootElement.GetProperty("fromSnapshotId").GetString());
        Assert.Equal(secondSnapshotId, diffBody.RootElement.GetProperty("toSnapshotId").GetString());
        Assert.Contains(changes, change =>
            change.GetProperty("area").GetString() == "Recipe"
            && change.GetProperty("field").GetString() == "RecipeVersionId"
            && change.GetProperty("changeType").GetString() == "Changed");
        Assert.Contains(changes, change =>
            change.GetProperty("area").GetString() == "Station"
            && change.GetProperty("field").GetString() == "StationProfileId"
            && change.GetProperty("changeType").GetString() == "Changed");
        Assert.Contains(changes, change =>
            change.GetProperty("area").GetString() == "DeviceBinding"
            && change.GetProperty("field").GetString() == "scanner-primary"
            && change.GetProperty("changeType").GetString() == "Removed");
        Assert.Contains(changes, change =>
            change.GetProperty("area").GetString() == "DeviceBinding"
            && change.GetProperty("field").GetString() == "multimeter-primary"
            && change.GetProperty("changeType").GetString() == "Added");
    }

    private static object CreateRecipeRequest(string recipeId)
    {
        return new
        {
            recipeId,
            versionId = $"{recipeId}@1.0.0",
            displayName = "End Of Line Recipe",
            parameters = new[]
            {
                new
                {
                    key = "voltage.max",
                    value = "5.2"
                }
            }
        };
    }

    private static object CreateStationProfileRequest(string stationProfileId)
    {
        return CreateStationProfileRequest(
            stationProfileId,
            "scanner-primary",
            "device.scanner",
            "scanner-01");
    }

    private static object CreateStationProfileRequest(
        string stationProfileId,
        string deviceBindingId,
        string capabilityId,
        string deviceKey)
    {
        return new
        {
            stationProfileId,
            displayName = "End Of Line Station",
            deviceBindings = new[]
            {
                new
                {
                    deviceBindingId,
                    capabilityId,
                    deviceKey
                }
            }
        };
    }

    private static object CreateWorkspaceRequest(string workspaceId)
    {
        return new
        {
            workspaceId,
            displayName = "Main Workspace"
        };
    }

    private static object CreateProjectRequest(string projectId, string workspaceId)
    {
        return new
        {
            projectId,
            workspaceId,
            displayName = "Packaging Line Project"
        };
    }

    private static object PublishSnapshotRequest(
        string snapshotId,
        string recipeId,
        string stationProfileId)
    {
        return new
        {
            snapshotId,
            processDefinitionId = "process-packaging",
            processVersionId = "process-packaging@1.0.0",
            recipeId,
            stationProfileId
        };
    }

    private static void AssertSnapshot(
        JsonDocument projectBody,
        string snapshotId,
        string recipeId,
        string stationProfileId)
    {
        var snapshot = Assert.Single(projectBody.RootElement.GetProperty("snapshots").EnumerateArray());
        var deviceBinding = Assert.Single(snapshot.GetProperty("deviceBindings").EnumerateArray());

        Assert.Equal(snapshotId, snapshot.GetProperty("snapshotId").GetString());
        Assert.Equal("process-packaging", snapshot.GetProperty("processDefinitionId").GetString());
        Assert.Equal("process-packaging@1.0.0", snapshot.GetProperty("processVersionId").GetString());
        Assert.Equal(recipeId, snapshot.GetProperty("recipeId").GetString());
        Assert.Equal($"{recipeId}@1.0.0", snapshot.GetProperty("recipeVersionId").GetString());
        Assert.Equal(stationProfileId, snapshot.GetProperty("stationProfileId").GetString());
        Assert.Equal("Published", snapshot.GetProperty("status").GetString());
        Assert.Equal("scanner-primary", deviceBinding.GetProperty("deviceBindingId").GetString());
        Assert.Equal("device.scanner", deviceBinding.GetProperty("capabilityId").GetString());
        Assert.Equal("scanner-01", deviceBinding.GetProperty("deviceKey").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
