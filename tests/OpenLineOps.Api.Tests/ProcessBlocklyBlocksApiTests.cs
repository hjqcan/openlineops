using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class ProcessBlocklyBlocksApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ProcessBlocklyBlocksApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task ListReturnsBuiltInBlocklyBlocks()
    {
        using var response = await _client.GetAsync("/api/process-blocks");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(body.RootElement.EnumerateArray(), block =>
            block.GetProperty("blockType").GetString() == "openlineops_move_axis"
            && block.GetProperty("isBuiltIn").GetBoolean()
            && block.GetProperty("version").GetInt32() == 1
            && block.GetProperty("blocklyJson").GetProperty("type").GetString() == "openlineops_move_axis");
    }

    [Fact]
    public async Task ListReturnsPluginManifestGeneratedBlocklyBlocks()
    {
        using var response = await _client.GetAsync("/api/process-blocks");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(body.RootElement.EnumerateArray(), block =>
            block.GetProperty("blockType").GetString() == "openlineops_plugin_device_openlineops_samples_loopback_device_loopback_echo"
            && block.GetProperty("category").GetString() == "Plugin Device Commands"
            && block.GetProperty("isBuiltIn").GetBoolean()
            && block.GetProperty("blocklyJson").GetProperty("type").GetString() == "openlineops_plugin_device_openlineops_samples_loopback_device_loopback_echo"
            && block.GetProperty("pythonCodeTemplate").GetString()!.Contains("command.execute", StringComparison.Ordinal)
            && block.GetProperty("pythonCodeTemplate").GetString()!.Contains("device.loopback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RegisterAddsUserDefinedBlocklyBlock()
    {
        var blockType = $"user_open_clamp_{Guid.NewGuid():N}";

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/process-blocks",
            new
            {
                blockType,
                category = "Fixture",
                displayName = "Open Clamp",
                blocklyJson = new
                {
                    type = blockType,
                    message0 = "open clamp %1",
                    args0 = new[]
                    {
                        new
                        {
                            type = "field_input",
                            name = "CLAMP",
                            text = "left"
                        }
                    },
                    previousStatement = (string?)null,
                    nextStatement = (string?)null,
                    colour = 130
                },
                pythonCodeTemplate = "automation_plan.append({'type': 'fixture.clamp', 'clamp': {{CLAMP}}, 'state': 'open'})"
            });
        using var createBody = await ReadJsonAsync(createResponse);

        using var listResponse = await _client.GetAsync("/api/process-blocks");
        using var listBody = await ReadJsonAsync(listResponse);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Contains($"/api/process-blocks/{blockType}", createResponse.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(blockType, createBody.RootElement.GetProperty("blockType").GetString());
        Assert.False(createBody.RootElement.GetProperty("isBuiltIn").GetBoolean());
        Assert.Equal(1, createBody.RootElement.GetProperty("version").GetInt32());
        Assert.NotEqual(default(DateTimeOffset), createBody.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.NotEqual(default(DateTimeOffset), createBody.RootElement.GetProperty("updatedAtUtc").GetDateTimeOffset());
        Assert.Equal(blockType, createBody.RootElement.GetProperty("blocklyJson").GetProperty("type").GetString());

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains(listBody.RootElement.EnumerateArray(), block =>
            block.GetProperty("blockType").GetString() == blockType
            && block.GetProperty("category").GetString() == "Fixture");
    }

    [Fact]
    public async Task RegisterExistingUserDefinedBlockCreatesNewVersion()
    {
        var blockType = $"user_close_clamp_{Guid.NewGuid():N}";

        using var firstResponse = await _client.PostAsJsonAsync(
            "/api/process-blocks",
            new
            {
                blockType,
                category = "Fixture",
                displayName = "Close Clamp",
                blocklyJson = new
                {
                    type = blockType,
                    message0 = "close clamp",
                    previousStatement = (string?)null,
                    nextStatement = (string?)null
                },
                pythonCodeTemplate = "automation_plan.append({'type': 'fixture.clamp', 'state': 'closed'})"
            });
        using var firstBody = await ReadJsonAsync(firstResponse);

        using var secondResponse = await _client.PostAsJsonAsync(
            "/api/process-blocks",
            new
            {
                blockType,
                category = "Fixture",
                displayName = "Close Clamp With Force",
                blocklyJson = new
                {
                    type = blockType,
                    message0 = "close clamp with force",
                    previousStatement = (string?)null,
                    nextStatement = (string?)null
                },
                pythonCodeTemplate = "automation_plan.append({'type': 'fixture.clamp', 'state': 'closed', 'force': 'normal'})"
            });
        using var secondBody = await ReadJsonAsync(secondResponse);

        using var listResponse = await _client.GetAsync("/api/process-blocks");
        using var listBody = await ReadJsonAsync(listResponse);
        using var versionsResponse = await _client.GetAsync($"/api/process-blocks/{blockType}/versions");
        using var versionsBody = await ReadJsonAsync(versionsResponse);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.Equal(1, firstBody.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(2, secondBody.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("Close Clamp With Force", secondBody.RootElement.GetProperty("displayName").GetString());
        Assert.Contains(listBody.RootElement.EnumerateArray(), block =>
            block.GetProperty("blockType").GetString() == blockType
            && block.GetProperty("displayName").GetString() == "Close Clamp With Force"
            && block.GetProperty("version").GetInt32() == 2);
        Assert.Equal(HttpStatusCode.OK, versionsResponse.StatusCode);
        Assert.Collection(
            versionsBody.RootElement.EnumerateArray(),
            block => Assert.Equal(2, block.GetProperty("version").GetInt32()),
            block => Assert.Equal(1, block.GetProperty("version").GetInt32()));
    }

    [Fact]
    public async Task ListVersionsForMissingBlockReturnsNotFound()
    {
        using var response = await _client.GetAsync($"/api/process-blocks/user_missing_{Guid.NewGuid():N}/versions");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NotFound.Processes.BlocklyBlockNotFound", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task RegisterRejectsBlocklyJsonTypeMismatch()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/process-blocks",
            new
            {
                blockType = "user_open_clamp",
                category = "Fixture",
                displayName = "Open Clamp",
                blocklyJson = new
                {
                    type = "different_block"
                },
                pythonCodeTemplate = "automation_plan.append({'type': 'fixture.clamp'})"
            });
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Validation.Processes.BlocklyBlockJsonTypeMismatch",
            body.RootElement.GetProperty("title").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
