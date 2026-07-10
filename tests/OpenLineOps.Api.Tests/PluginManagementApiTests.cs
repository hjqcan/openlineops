using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class PluginManagementApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PluginManagementApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task OverviewReturnsSamplePluginInventory()
    {
        using var response = await _client.GetAsync("/api/plugins/overview");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var package = body.RootElement
            .GetProperty("packages")
            .EnumerateArray()
            .Single(item => item.GetProperty("manifest").GetProperty("id").GetString()
                == "openlineops.samples.loopback-device");
        Assert.True(package.GetProperty("isValid").GetBoolean());
        Assert.Equal("DeviceDriver", package.GetProperty("manifest").GetProperty("kind").GetString());
        Assert.Equal("device.loopback", body.RootElement.GetProperty("capabilities")[0].GetProperty("capability").GetString());
        Assert.Equal("Echo", body.RootElement.GetProperty("deviceCommands")[0].GetProperty("commandName").GetString());
        Assert.Empty(body.RootElement.GetProperty("processCommands").EnumerateArray());
    }

    [Fact]
    public async Task LifecycleStartAndStopReturnManifestOnlyState()
    {
        using var startResponse = await _client.PostAsync("/api/plugins/lifecycle/start", content: null);
        using var startBody = await ReadJsonAsync(startResponse);
        using var stopResponse = await _client.PostAsync("/api/plugins/lifecycle/stop", content: null);
        using var stopBody = await ReadJsonAsync(stopResponse);

        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        var startRecord = startBody.RootElement.EnumerateArray().Single(record =>
            record.GetProperty("manifest").GetProperty("id").GetString()
            == "openlineops.samples.loopback-device");
        Assert.Equal("Initialized", startRecord.GetProperty("state").GetString());
        Assert.Equal("Initialized", startRecord.GetProperty("initializationStatus").GetString());

        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
        var stopRecord = stopBody.RootElement.EnumerateArray().Single(record =>
            record.GetProperty("manifest").GetProperty("id").GetString()
            == "openlineops.samples.loopback-device");
        Assert.Equal("Stopped", stopRecord.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ProcessEventsRejectsInvalidPageSize()
    {
        using var response = await _client.GetAsync("/api/plugins/process-events?take=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProcessEventsRequireExactCanonicalKindToken()
    {
        using var canonical = await _client.GetAsync("/api/plugins/process-events?kind=Started");
        using var caseChanged = await _client.GetAsync("/api/plugins/process-events?kind=started");
        var caseChangedBody = await caseChanged.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, caseChanged.StatusCode);
        Assert.Contains("case-sensitive", caseChangedBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    [InlineData("Started&kind=Stopped")]
    public async Task ProcessEventsRejectExplicitNonCanonicalKind(string queryValue)
    {
        using var response = await _client.GetAsync($"/api/plugins/process-events?kind={queryValue}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    [InlineData("%20plugin-alpha")]
    [InlineData("plugin-alpha%20")]
    [InlineData("plugin-alpha&pluginId=plugin-beta")]
    public async Task ProcessEventsRejectExplicitNonCanonicalPluginId(string queryValue)
    {
        using var response = await _client.GetAsync($"/api/plugins/process-events?pluginId={queryValue}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PluginId", body, StringComparison.Ordinal);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
