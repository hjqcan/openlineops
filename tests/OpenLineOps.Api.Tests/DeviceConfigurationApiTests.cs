using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class DeviceConfigurationApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DeviceConfigurationApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task DeviceDefinitionAndInstanceLifecycleCanBeManaged()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var definitionId = $"device-definition-api-{suffix}";
        var instanceId = $"device-instance-api-{suffix}";

        using var createDefinitionResponse = await _client.PostAsJsonAsync(
            "/api/devices/definitions",
            CreateDefinitionRequest(definitionId));
        using var createDefinitionBody = await ReadJsonAsync(createDefinitionResponse);

        Assert.Equal(HttpStatusCode.Created, createDefinitionResponse.StatusCode);
        Assert.Equal(definitionId, createDefinitionBody.RootElement.GetProperty("deviceDefinitionId").GetString());
        Assert.Single(createDefinitionBody.RootElement.GetProperty("capabilities").EnumerateArray());
        Assert.Single(createDefinitionBody.RootElement.GetProperty("commands").EnumerateArray());

        using var registerInstanceResponse = await _client.PostAsJsonAsync(
            "/api/devices/instances",
            RegisterInstanceRequest(instanceId, definitionId));
        using var registerInstanceBody = await ReadJsonAsync(registerInstanceResponse);

        Assert.Equal(HttpStatusCode.Created, registerInstanceResponse.StatusCode);
        Assert.Equal(instanceId, registerInstanceBody.RootElement.GetProperty("deviceInstanceId").GetString());
        Assert.Equal("Disconnected", registerInstanceBody.RootElement.GetProperty("status").GetString());

        using var connectResponse = await _client.PostAsync(
            $"/api/devices/instances/{instanceId}/connect",
            content: null);
        using var connectBody = await ReadJsonAsync(connectResponse);

        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        Assert.Equal("Connected", connectBody.RootElement.GetProperty("status").GetString());

        using var faultResponse = await _client.PostAsJsonAsync(
            $"/api/devices/instances/{instanceId}/faults",
            new
            {
                reason = "smoke fault"
            });
        using var faultBody = await ReadJsonAsync(faultResponse);

        Assert.Equal(HttpStatusCode.OK, faultResponse.StatusCode);
        Assert.Equal("Faulted", faultBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("smoke fault", faultBody.RootElement.GetProperty("faultReason").GetString());

        using var resetResponse = await _client.PostAsync(
            $"/api/devices/instances/{instanceId}/fault-reset",
            content: null);
        using var resetBody = await ReadJsonAsync(resetResponse);

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        Assert.Equal("Disconnected", resetBody.RootElement.GetProperty("status").GetString());

        using var listDefinitionsResponse = await _client.GetAsync("/api/devices/definitions");
        using var listInstancesResponse = await _client.GetAsync("/api/devices/instances");
        using var listDefinitionsBody = await ReadJsonAsync(listDefinitionsResponse);
        using var listInstancesBody = await ReadJsonAsync(listInstancesResponse);

        Assert.Equal(HttpStatusCode.OK, listDefinitionsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listInstancesResponse.StatusCode);
        Assert.Contains(
            listDefinitionsBody.RootElement.EnumerateArray(),
            item => item.GetProperty("deviceDefinitionId").GetString() == definitionId);
        Assert.Contains(
            listInstancesBody.RootElement.EnumerateArray(),
            item => item.GetProperty("deviceInstanceId").GetString() == instanceId);
    }

    [Fact]
    public async Task DeviceInstanceCannotReferenceMissingDefinition()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var response = await _client.PostAsJsonAsync(
            "/api/devices/instances",
            RegisterInstanceRequest(
                $"device-instance-missing-{suffix}",
                $"device-definition-missing-{suffix}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static object CreateDefinitionRequest(string definitionId)
    {
        return new
        {
            deviceDefinitionId = definitionId,
            displayName = "Loopback Scanner",
            pluginId = "openlineops.loopback",
            capabilities = new[]
            {
                new
                {
                    capabilityId = "device.loopback",
                    displayName = "Loopback Device"
                }
            },
            commands = new[]
            {
                new
                {
                    commandDefinitionId = "loopback.echo",
                    capabilityId = "device.loopback",
                    commandName = "Echo",
                    inputSchema = (string?)null,
                    outputSchema = (string?)null,
                    timeoutSeconds = 15,
                    maxRetries = 1
                }
            }
        };
    }

    private static object RegisterInstanceRequest(string instanceId, string definitionId)
    {
        return new
        {
            deviceInstanceId = instanceId,
            deviceDefinitionId = definitionId,
            stationId = "station-api-devices",
            displayName = "Loopback 01",
            protocol = "simulator",
            address = "loopback://scanner-01"
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
