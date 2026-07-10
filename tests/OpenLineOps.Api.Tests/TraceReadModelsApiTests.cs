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
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task StationDashboardReturnsProductionRunCountsAndNestedStageEvidenceTotals()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station-dashboard-{suffix}";
        using var passed = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-DASH-P-{suffix}",
                $"batch-p-{suffix}",
                BaseTimeUtc.AddMinutes(1),
                stationId: stationId));
        using var failed = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-DASH-F-{suffix}",
                $"batch-f-{suffix}",
                BaseTimeUtc.AddMinutes(2),
                commandStatus: "Failed",
                judgement: "Failed",
                stationId: stationId));

        using var response = await _client.GetAsync(
            $"/api/traceability/read-models/station-dashboard?stationId={stationId}&recentLimit=2");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, passed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("passedCount").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("failedCount").GetInt64());
        var latest = body.RootElement.GetProperty("recentTraces")[0];
        Assert.Equal($"SMX-DASH-F-{suffix}", latest.GetProperty("dutIdentityValue").GetString());
        Assert.Equal(1, latest.GetProperty("stageCount").GetInt32());
        Assert.Equal(1, latest.GetProperty("failedCommandCount").GetInt32());
        Assert.Equal(1, latest.GetProperty("failedMeasurementCount").GetInt32());
        Assert.Equal(1, latest.GetProperty("incidentCount").GetInt32());
    }

    [Fact]
    public async Task EngineeringSearchReturnsProductionRunRowsAndStageFacets()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var batchId = $"batch-engineering-{suffix}";
        using var first = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-ENG-P-{suffix}",
                batchId,
                BaseTimeUtc.AddMinutes(1),
                stationId: "station-engineering-a",
                processVersionId: "process-api-read-model@1.0.0"));
        using var second = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-ENG-F-{suffix}",
                batchId,
                BaseTimeUtc.AddMinutes(2),
                commandStatus: "Failed",
                judgement: "Failed",
                stationId: "station-engineering-b",
                processVersionId: "process-api-read-model@1.0.0"));

        using var response = await _client.GetAsync(
            $"/api/traceability/read-models/engineering-search?batchId={batchId}"
            + "&processVersionId=process-api-read-model@1.0.0&pageSize=10");
        using var body = await ReadJsonAsync(response);
        var results = body.RootElement.GetProperty("results");
        var facets = body.RootElement.GetProperty("facets");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, results.GetProperty("totalCount").GetInt64());
        Assert.All(results.GetProperty("items").EnumerateArray(), row =>
            Assert.Equal(1, row.GetProperty("stageCount").GetInt32()));
        Assert.Equal("Failed", facets.GetProperty("judgements")[0].GetProperty("value").GetString());
        Assert.Equal(2, facets.GetProperty("processVersions")[0].GetProperty("count").GetInt64());
        Assert.Equal(2, facets.GetProperty("productionLines")[0].GetProperty("count").GetInt64());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
