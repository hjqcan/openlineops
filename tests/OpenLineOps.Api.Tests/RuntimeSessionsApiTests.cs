using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class RuntimeSessionsApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RuntimeSessionsApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = DevelopmentRuntimeStartTestHost.Create(factory);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task StartSimulatedSessionReturnsCreatedAndCanBeQueried()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            CreateStartRequest("api-success", "scan-ok", "measure-ok"));

        using var body = await ReadJsonAsync(response);
        var sessionId = body.RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains($"/api/runtime/sessions/{sessionId}", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal("Completed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("commandCount").GetInt32());

        using var queryResponse = await _client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionBody = await ReadJsonAsync(queryResponse);

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(sessionId, sessionBody.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal("snapshot-api-success", sessionBody.RootElement.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal("Completed", sessionBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, sessionBody.RootElement.GetProperty("steps").GetArrayLength());
        Assert.Equal(2, sessionBody.RootElement.GetProperty("commands").GetArrayLength());
        Assert.Equal(0, sessionBody.RootElement.GetProperty("incidents").GetArrayLength());
        var firstStep = sessionBody.RootElement.GetProperty("steps")[0];
        Assert.Equal("node-scan:action:1", firstStep.GetProperty("actionId").GetString());
        Assert.Equal(JsonValueKind.Null, firstStep.GetProperty("parentStepId").ValueKind);
        Assert.Equal(JsonValueKind.Null, firstStep.GetProperty("dynamicSequence").ValueKind);
        var firstCommand = sessionBody.RootElement.GetProperty("commands")[0];
        Assert.Equal(firstStep.GetProperty("actionId").GetString(), firstCommand.GetProperty("actionId").GetString());
    }

    [Fact]
    public async Task StartSimulatedSessionWithTraceMetadataCreatesTraceRecord()
    {
        var serialNumber = $"SN-{Guid.NewGuid():N}";
        using var response = await _client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            CreateStartRequestWithTraceMetadata("api-trace", serialNumber, "scan-ok", "measure-ok"));

        using var body = await ReadJsonAsync(response);
        var sessionId = body.RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Completed", body.RootElement.GetProperty("status").GetString());

        using var queryResponse = await _client.GetAsync(
            $"/api/traceability/records?serialNumber={Uri.EscapeDataString(serialNumber)}");
        using var queryBody = await ReadJsonAsync(queryResponse);
        var traceSummary = Assert.Single(queryBody.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(sessionId, traceSummary.GetProperty("traceRecordId").GetGuid());
        Assert.Equal(sessionId, traceSummary.GetProperty("runtimeSessionId").GetGuid());
        Assert.Equal(serialNumber, traceSummary.GetProperty("serialNumber").GetString());
        Assert.Equal("fixture-api-trace", traceSummary.GetProperty("fixtureId").GetString());
        Assert.Equal("device-api-trace", traceSummary.GetProperty("deviceId").GetString());
        Assert.Equal("Passed", traceSummary.GetProperty("judgement").GetString());

        using var exportResponse = await _client.GetAsync($"/api/traceability/records/{sessionId}/export");
        using var exportBody = await ReadJsonAsync(exportResponse);
        var traceRecord = exportBody.RootElement.GetProperty("traceRecord");
        var measurements = traceRecord.GetProperty("measurements").EnumerateArray().ToArray();
        var textValues = measurements
            .Select(measurement => measurement.GetProperty("textValue").GetString())
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal(sessionId, traceRecord.GetProperty("traceRecordId").GetGuid());
        Assert.Equal("operator-api-trace", traceRecord.GetProperty("recordedBy").GetString());
        Assert.Equal(2, measurements.Length);
        Assert.Contains("scan-ok", textValues);
        Assert.Contains("measure-ok", textValues);
    }

    [Fact]
    public async Task StartSimulatedSessionWithFailingNodeReturnsFailedSessionAndRaisesOperationsAlarm()
    {
        var suffix = $"api-failure-{Guid.NewGuid():N}";
        var stationId = $"station-{suffix}";

        using var response = await _client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            CreateStartRequest(suffix, "fail", "should-not-run"));

        using var body = await ReadJsonAsync(response);
        var sessionId = body.RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Failed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("commandCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("incidentCount").GetInt32());

        using var queryResponse = await _client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionBody = await ReadJsonAsync(queryResponse);
        var incidents = sessionBody.RootElement.GetProperty("incidents");

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal("Failed", sessionBody.RootElement.GetProperty("status").GetString());
        var incident = Assert.Single(incidents.EnumerateArray());
        var incidentId = incident.GetProperty("incidentId").GetGuid();

        Assert.Equal("Runtime.CommandFailed", incident.GetProperty("code").GetString());

        using var openAlarmsResponse = await _client.GetAsync(
            $"/api/operations/alarms/open?stationId={Uri.EscapeDataString(stationId)}");
        using var openAlarmsBody = await ReadJsonAsync(openAlarmsResponse);
        var alarm = Assert.Single(openAlarmsBody.RootElement.EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, openAlarmsResponse.StatusCode);
        Assert.Equal($"operations.alarm.runtime.incident.{incidentId:N}", alarm.GetProperty("id").GetString());
        Assert.Equal(stationId, alarm.GetProperty("stationId").GetString());
        Assert.Equal("runtime", alarm.GetProperty("source").GetString());
        Assert.Equal(sessionId.ToString("D"), alarm.GetProperty("sourceId").GetString());
        Assert.Equal(2, alarm.GetProperty("severity").GetInt32());
        Assert.Equal(0, alarm.GetProperty("status").GetInt32());
        Assert.Equal("Runtime incident: Runtime.CommandFailed", alarm.GetProperty("title").GetString());
        Assert.Equal("Simulated command failure.", alarm.GetProperty("description").GetString());
    }

    [Fact]
    public async Task RecoveryPlanIgnoresCompletedAndFailedSimulatedSessions()
    {
        using var completedResponse = await _client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            CreateStartRequest("api-recovery-completed", "scan-ok", "measure-ok"));
        using var failedResponse = await _client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            CreateStartRequest("api-recovery-failed", "fail", "should-not-run"));

        using var response = await _client.GetAsync("/api/runtime/sessions/recovery-plan");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, completedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, failedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public async Task StartSimulatedSessionWithNoNodesReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            new
            {
                stationId = "station-api-validation",
                configurationSnapshotId = "snapshot-api-validation",
                recipeSnapshotId = "recipe-api-validation",
                processDefinitionId = "process-api-validation",
                processVersionId = "process-api-validation@1.0.0",
                nodes = Array.Empty<object>()
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object CreateStartRequest(
        string suffix,
        string firstInputPayload,
        string secondInputPayload)
    {
        return new
        {
            stationId = $"station-{suffix}",
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

    private static object CreateStartRequestWithTraceMetadata(
        string suffix,
        string serialNumber,
        string firstInputPayload,
        string secondInputPayload)
    {
        return new
        {
            stationId = $"station-{suffix}",
            configurationSnapshotId = $"snapshot-{suffix}",
            recipeSnapshotId = $"recipe-{suffix}",
            processDefinitionId = $"process-{suffix}",
            processVersionId = $"process-{suffix}@1.0.0",
            serialNumber,
            batchId = $"batch-{suffix}",
            fixtureId = $"fixture-{suffix}",
            deviceId = $"device-{suffix}",
            actorId = $"operator-{suffix}",
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
}
