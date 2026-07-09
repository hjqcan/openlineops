using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class TraceReadModelsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _client;

    public TraceReadModelsApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task StationDashboardReturnsCountsAndRecentRows()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station-dashboard-{suffix}";
        using var passed = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest($"SMX-DASH-P-{suffix}", stationId, "Passed", BaseTimeUtc.AddMinutes(1), passed: true));
        using var failed = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest($"SMX-DASH-F-{suffix}", stationId, "Failed", BaseTimeUtc.AddMinutes(2), passed: false));

        using var response = await _client.GetAsync(
            $"/api/traceability/read-models/station-dashboard?stationId={stationId}&recentLimit=2");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, passed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("passedCount").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("failedCount").GetInt64());
        Assert.Equal($"SMX-DASH-F-{suffix}", body.RootElement.GetProperty("recentTraces")[0].GetProperty("serialNumber").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("recentTraces")[0].GetProperty("failedMeasurementCount").GetInt32());
    }

    [Fact]
    public async Task EngineeringSearchReturnsRowsAndFacets()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var batchId = $"batch-engineering-{suffix}";
        using var first = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest($"SMX-ENG-P-{suffix}", "station-engineering-a", "Passed", BaseTimeUtc.AddMinutes(1), passed: true, batchId: batchId));
        using var second = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest($"SMX-ENG-F-{suffix}", "station-engineering-b", "Failed", BaseTimeUtc.AddMinutes(2), passed: false, batchId: batchId));

        using var response = await _client.GetAsync(
            $"/api/traceability/read-models/engineering-search?batchId={batchId}&processVersionId=process-api-read-model@1.0.0&pageSize=10");
        using var body = await ReadJsonAsync(response);
        var results = body.RootElement.GetProperty("results");
        var facets = body.RootElement.GetProperty("facets");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, results.GetProperty("totalCount").GetInt64());
        Assert.Equal(2, results.GetProperty("items").GetArrayLength());
        Assert.Equal("Failed", facets.GetProperty("judgements")[0].GetProperty("value").GetString());
        Assert.Equal(1, facets.GetProperty("judgements")[0].GetProperty("count").GetInt64());
        Assert.Equal(2, facets.GetProperty("processVersions")[0].GetProperty("count").GetInt64());
    }

    private static object CreateTraceRequest(
        string serialNumber,
        string stationId,
        string judgement,
        DateTimeOffset completedAtUtc,
        bool passed,
        string? batchId = null)
    {
        return new
        {
            runtimeSessionId = Guid.NewGuid(),
            serialNumber,
            batchId = batchId ?? $"batch-{serialNumber}",
            stationId,
            fixtureId = "fixture-api-read-model",
            processDefinitionId = "process-api-read-model",
            processVersionId = "process-api-read-model@1.0.0",
            configurationSnapshotId = "config-api-read-model",
            recipeSnapshotId = "recipe-api-read-model",
            deviceId = "device-api-read-model",
            judgement,
            startedAtUtc = BaseTimeUtc,
            completedAtUtc,
            recordedBy = "operator-api-read-model",
            measurements = new[]
            {
                new
                {
                    name = "voltage",
                    numericValue = 3.3m,
                    textValue = (string?)null,
                    unit = "V",
                    deviceId = "device-api-read-model",
                    runtimeCommandId = Guid.NewGuid(),
                    passed,
                    measuredAtUtc = completedAtUtc.AddSeconds(-5)
                }
            },
            artifacts = new[]
            {
                new
                {
                    name = "log",
                    kind = "Log",
                    storageKey = $"trace/{serialNumber}/log.txt",
                    mediaType = "text/plain",
                    sizeBytes = 128,
                    sha256 = "sha256-read-model",
                    deviceId = "device-api-read-model",
                    capturedAtUtc = completedAtUtc
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
