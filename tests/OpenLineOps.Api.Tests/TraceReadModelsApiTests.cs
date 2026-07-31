using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class TraceReadModelsApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private readonly OpenLineOpsApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TraceReadModelsApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task StationDashboardSeparatesProductJudgementFromExecutionFailureAndReturnsOperationEvidence()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationSystemId = $"station-system-dashboard-{suffix}";
        var passed = await TraceRecordsApiTests.ProjectTraceAsync(
            _factory,
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-DASH-P-{suffix}",
                $"batch-p-{suffix}",
                BaseTimeUtc.AddMinutes(1),
                stationSystemId: stationSystemId));
        var nonconforming = await TraceRecordsApiTests.ProjectTraceAsync(
            _factory,
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-DASH-F-{suffix}",
                $"batch-f-{suffix}",
                BaseTimeUtc.AddMinutes(2),
                judgement: "Failed",
                stationSystemId: stationSystemId));
        var systemFailure = await TraceRecordsApiTests.ProjectTraceAsync(
            _factory,
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-DASH-U-{suffix}",
                $"batch-u-{suffix}",
                BaseTimeUtc.AddMinutes(3),
                commandStatus: "Failed",
                stationSystemId: stationSystemId));

        using var response = await _client.GetAsync(
            $"/api/traceability/read-models/station-dashboard?stationSystemId={stationSystemId}&recentLimit=3");
        using var body = await ReadJsonAsync(response);

        Assert.True(passed.IsSuccess, passed.Error.Message);
        Assert.True(nonconforming.IsSuccess, nonconforming.Error.Message);
        Assert.True(systemFailure.IsSuccess, systemFailure.Error.Message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, body.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("passedCount").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("failedCount").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("unknownCount").GetInt64());
        var latest = body.RootElement.GetProperty("recentTraces")[0];
        Assert.Equal(
            $"SMX-DASH-U-{suffix}",
            latest.GetProperty("productionUnitIdentityValue").GetString());
        Assert.Equal("Failed", latest.GetProperty("executionStatus").GetString());
        Assert.Equal("Unknown", latest.GetProperty("judgement").GetString());
        Assert.Equal(1, latest.GetProperty("operationCount").GetInt32());
        Assert.Equal(1, latest.GetProperty("failedCommandCount").GetInt32());
        Assert.Equal(1, latest.GetProperty("incidentCount").GetInt32());
        var productFailure = body.RootElement.GetProperty("recentTraces")[1];
        Assert.Equal("Completed", productFailure.GetProperty("executionStatus").GetString());
        Assert.Equal("Failed", productFailure.GetProperty("judgement").GetString());
        Assert.Equal("Nonconforming", productFailure.GetProperty("disposition").GetString());
        Assert.Equal(0, productFailure.GetProperty("failedCommandCount").GetInt32());
        Assert.Equal(1, productFailure.GetProperty("failedMeasurementCount").GetInt32());
    }

    [Fact]
    public async Task EngineeringSearchReturnsProductionRunRowsAndOperationFacets()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var lotId = $"lot-engineering-{suffix}";
        var first = await TraceRecordsApiTests.ProjectTraceAsync(
            _factory,
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-ENG-P-{suffix}",
                lotId,
                BaseTimeUtc.AddMinutes(1),
                stationSystemId: "station-system-engineering-a",
                processVersionId: "process-api-read-model@1.0.0"));
        var second = await TraceRecordsApiTests.ProjectTraceAsync(
            _factory,
            TraceRecordsApiTests.CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-ENG-F-{suffix}",
                lotId,
                BaseTimeUtc.AddMinutes(2),
                judgement: "Failed",
                stationSystemId: "station-system-engineering-b",
                processVersionId: "process-api-read-model@1.0.0"));

        using var response = await _client.GetAsync(
            $"/api/traceability/read-models/engineering-search?lotId={lotId}"
            + "&processVersionId=process-api-read-model@1.0.0&pageSize=10");
        using var body = await ReadJsonAsync(response);
        var results = body.RootElement.GetProperty("results");
        var facets = body.RootElement.GetProperty("facets");

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(second.IsSuccess, second.Error.Message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, results.GetProperty("totalCount").GetInt64());
        Assert.All(results.GetProperty("items").EnumerateArray(), row =>
            Assert.Equal(1, row.GetProperty("operationCount").GetInt32()));
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
