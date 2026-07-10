using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace OpenLineOps.Api.Tests;

public sealed class RuntimeMonitoringApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RuntimeMonitoringApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task MonitoringEndpointsExposeStationTimelineAndAlarmAcknowledgement()
    {
        using var developmentFactory = DevelopmentRuntimeStartTestHost.Create(_factory);
        using var client = developmentFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station-monitoring-{suffix}";
        using var startResponse = await client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            CreateStartRequest(stationId, suffix, "fail", "should-not-run"));
        using var startBody = await ReadJsonAsync(startResponse);
        var sessionId = startBody.RootElement.GetProperty("sessionId").GetGuid();

        using var stationResponse = await client.GetAsync(
            $"/api/runtime/monitoring/stations?stationId={Uri.EscapeDataString(stationId)}");
        using var stationBody = await ReadJsonAsync(stationResponse);
        var station = Assert.Single(stationBody.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stationResponse.StatusCode);
        Assert.Equal(stationId, station.GetProperty("stationId").GetString());
        Assert.Equal(sessionId, station.GetProperty("latestSessionId").GetGuid());
        Assert.Equal("Failed", station.GetProperty("sessionStatus").GetString());
        Assert.Equal(1, station.GetProperty("incidentCount").GetInt32());
        Assert.True(station.GetProperty("isTerminal").GetBoolean());

        using var timelineResponse = await client.GetAsync(
            $"/api/runtime/monitoring/sessions/{sessionId}/timeline");
        using var timelineBody = await ReadJsonAsync(timelineResponse);
        var timelineEvents = timelineBody.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
        Assert.Contains(timelineEvents, entry => entry.GetProperty("eventName").GetString() == "RuntimeIncident.Recorded");
        Assert.Contains(timelineEvents, entry =>
            entry.GetProperty("eventName").GetString() == "RuntimeSession.StatusChanged"
            && entry.GetProperty("toStatus").GetString() == "Failed");

        using var alarmsResponse = await client.GetAsync(
            $"/api/runtime/monitoring/alarms?stationId={Uri.EscapeDataString(stationId)}");
        using var alarmsBody = await ReadJsonAsync(alarmsResponse);
        var alarm = Assert.Single(alarmsBody.RootElement.GetProperty("items").EnumerateArray());
        var alarmId = alarm.GetProperty("alarmId").GetGuid();

        Assert.Equal(HttpStatusCode.OK, alarmsResponse.StatusCode);
        Assert.Equal("Runtime.CommandFailed", alarm.GetProperty("code").GetString());
        Assert.False(alarm.GetProperty("isAcknowledged").GetBoolean());

        using var acknowledgeResponse = await client.PostAsJsonAsync(
            $"/api/runtime/monitoring/alarms/{alarmId}/acknowledgements",
            new { acknowledgedBy = "operator-api" });
        using var acknowledgeBody = await ReadJsonAsync(acknowledgeResponse);

        Assert.Equal(HttpStatusCode.OK, acknowledgeResponse.StatusCode);
        Assert.True(acknowledgeBody.RootElement.GetProperty("isAcknowledged").GetBoolean());
        Assert.Equal("operator-api", acknowledgeBody.RootElement.GetProperty("acknowledgedBy").GetString());

        using var acknowledgedAlarmsResponse = await client.GetAsync(
            $"/api/runtime/monitoring/alarms?stationId={Uri.EscapeDataString(stationId)}&includeAcknowledged=true");
        using var acknowledgedAlarmsBody = await ReadJsonAsync(acknowledgedAlarmsResponse);
        var acknowledgedAlarm = Assert.Single(acknowledgedAlarmsBody.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(alarmId, acknowledgedAlarm.GetProperty("alarmId").GetGuid());
        Assert.True(acknowledgedAlarm.GetProperty("isAcknowledged").GetBoolean());
    }

    [Fact]
    public async Task RuntimeProgressHubPublishesStationStatusAndTimelineEvents()
    {
        using var developmentFactory = DevelopmentRuntimeStartTestHost.Create(_factory);
        using var client = developmentFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station-realtime-{suffix}";
        var statusReceived = new TaskCompletionSource<RuntimeStationStatusSignal>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var eventReceived = new TaskCompletionSource<RuntimeTimelineSignal>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var connection = CreateHubConnection(developmentFactory, client);
        connection.On<RuntimeStationStatusSignal>("StationStatusChanged", status =>
        {
            if (status.StationId == stationId && status.SessionStatus == "Completed")
            {
                statusReceived.TrySetResult(status);
            }
        });
        connection.On<RuntimeTimelineSignal>("RuntimeEvent", entry =>
        {
            if (entry.StationId == stationId
                && entry.EventName == "RuntimeSession.StatusChanged"
                && entry.ToStatus == "Completed")
            {
                eventReceived.TrySetResult(entry);
            }
        });

        await connection.StartAsync();

        using var response = await client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            CreateStartRequest(stationId, suffix, "scan-ok", "measure-ok"));

        var stationStatus = await statusReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var runtimeEvent = await eventReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(stationId, stationStatus.StationId);
        Assert.Equal("Completed", stationStatus.SessionStatus);
        Assert.Equal(stationStatus.LatestSessionId, runtimeEvent.SessionId);
        Assert.Equal("Completed", runtimeEvent.ToStatus);
    }

    [Fact]
    public async Task RuntimeProgressHubCorsAllowsDesktopViteOrigin()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Options,
            "/hubs/runtime-progress/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "http://127.0.0.1:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "http://127.0.0.1:5173",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var credentials));
        Assert.Equal("true", credentials.Single());
    }

    private static HubConnection CreateHubConnection(
        WebApplicationFactory<Program> factory,
        HttpClient client)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, "/hubs/runtime-progress"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
    }

    private static object CreateStartRequest(
        string stationId,
        string suffix,
        string firstInputPayload,
        string secondInputPayload)
    {
        return new
        {
            stationId,
            configurationSnapshotId = $"snapshot-{suffix}",
            recipeSnapshotId = $"recipe-{suffix}",
            processDefinitionId = $"process-{suffix}",
            processVersionId = $"process-{suffix}@1.0.0",
            nodes = new[]
            {
                new
                {
                    nodeId = "node-scan",
                    displayName = "Scan barcode",
                    targetCapability = "device.scanner",
                    commandName = "Scan",
                    timeoutSeconds = 30,
                    inputPayload = firstInputPayload
                },
                new
                {
                    nodeId = "node-measure",
                    displayName = "Measure voltage",
                    targetCapability = "device.multimeter",
                    commandName = "MeasureVoltage",
                    timeoutSeconds = 30,
                    inputPayload = secondInputPayload
                }
            }
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private sealed record RuntimeStationStatusSignal(
        string StationId,
        Guid LatestSessionId,
        string SessionStatus);

    private sealed record RuntimeTimelineSignal(
        Guid SessionId,
        string StationId,
        string EventName,
        string? ToStatus);
}
