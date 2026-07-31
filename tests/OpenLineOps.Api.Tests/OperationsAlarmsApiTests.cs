using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class OperationsAlarmsApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private readonly HttpClient _operatorClient;
    private readonly HttpClient _stationAgentClient;

    public OperationsAlarmsApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        var options = new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        };
        _operatorClient = factory.CreateAuthenticatedClient(options, ApiTestAuthentication.OperatorToken);
        _stationAgentClient = factory.CreateAuthenticatedClient(options, ApiTestAuthentication.StationAgentToken);
    }

    [Fact]
    public async Task AlarmLifecycleAllowsStationSourceClearanceButNotOperatorResolution()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var alarmId = $"operations.alarm.api.{suffix}";
        var stationId = ApiTestAuthentication.StationAgentStationId;

        using var forbiddenRaiseResponse = await _operatorClient.PostAsJsonAsync(
            "/api/operations/alarms",
            new
            {
                id = $"{alarmId}.forbidden",
                stationId,
                source = "runtime",
                sourceId = $"session-{suffix}",
                severity = 3,
                title = "Runtime command failed",
                description = "The command failed during execution."
            });

        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRaiseResponse.StatusCode);

        using var raiseResponse = await _stationAgentClient.PostAsJsonAsync("/api/operations/alarms", new
        {
            id = alarmId,
            stationId,
            source = "runtime",
            sourceId = $"session-{suffix}",
            severity = 3,
            title = "Runtime command failed",
            description = "The command failed during execution."
        });
        using var raiseBody = await ReadJsonAsync(raiseResponse);

        Assert.Equal(HttpStatusCode.Created, raiseResponse.StatusCode);
        Assert.Equal(alarmId, raiseBody.RootElement.GetProperty("id").GetString());
        Assert.Equal(stationId, raiseBody.RootElement.GetProperty("stationId").GetString());
        Assert.Equal(0, raiseBody.RootElement.GetProperty("status").GetInt32());

        using var openResponse = await _operatorClient.GetAsync(
            $"/api/operations/alarms/open?stationId={Uri.EscapeDataString(stationId)}");
        using var openBody = await ReadJsonAsync(openResponse);
        var openAlarm = Assert.Single(openBody.RootElement.EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, openResponse.StatusCode);
        Assert.Equal(alarmId, openAlarm.GetProperty("id").GetString());

        using var acknowledgeResponse = await _operatorClient.PostAsJsonAsync(
            $"/api/operations/alarms/{Uri.EscapeDataString(alarmId)}/acknowledgement",
            new { });
        using var acknowledgeBody = await ReadJsonAsync(acknowledgeResponse);

        Assert.Equal(HttpStatusCode.OK, acknowledgeResponse.StatusCode);
        Assert.True(acknowledgeBody.RootElement.GetProperty("succeeded").GetBoolean());

        using var forbiddenClearResponse = await _operatorClient.PostAsJsonAsync(
            $"/api/operations/alarms/{Uri.EscapeDataString(alarmId)}/source-clearance",
            new
            {
                clearanceNote = "Recovered and verified."
            });

        Assert.Equal(HttpStatusCode.Forbidden, forbiddenClearResponse.StatusCode);

        using var clearResponse = await _stationAgentClient.PostAsJsonAsync(
            $"/api/operations/alarms/{Uri.EscapeDataString(alarmId)}/source-clearance",
            new
            {
                clearanceNote = "Recovered and verified."
            });
        using var clearBody = await ReadJsonAsync(clearResponse);

        Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
        Assert.True(clearBody.RootElement.GetProperty("succeeded").GetBoolean());

        using var resolvedResponse = await _operatorClient.GetAsync(
            $"/api/operations/alarms/{Uri.EscapeDataString(alarmId)}");
        using var resolvedBody = await ReadJsonAsync(resolvedResponse);
        Assert.Equal(
            ApiTestAuthentication.OperatorActorId,
            resolvedBody.RootElement.GetProperty("acknowledgedBy").GetString());
        Assert.Equal(
            ApiTestAuthentication.StationAgentActorId,
            resolvedBody.RootElement.GetProperty("resolvedBy").GetString());

        using var resolvedOpenResponse = await _operatorClient.GetAsync(
            $"/api/operations/alarms/open?stationId={Uri.EscapeDataString(stationId)}");
        using var resolvedOpenBody = await ReadJsonAsync(resolvedOpenResponse);

        Assert.Empty(resolvedOpenBody.RootElement.EnumerateArray());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
