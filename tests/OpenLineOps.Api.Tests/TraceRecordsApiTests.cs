using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class TraceRecordsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _client;

    public TraceRecordsApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task CreateTraceRecordReturnsCreatedAndCanBeQueriedAndExported()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var serialNumber = $"SMX-API-{suffix}";

        using var response = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRecordRequest(serialNumber, "batch-api-main", BaseTimeUtc.AddMinutes(3)));
        using var body = await ReadJsonAsync(response);
        var traceRecordId = body.RootElement.GetProperty("traceRecordId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains($"/api/traceability/records/{traceRecordId}", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(serialNumber, body.RootElement.GetProperty("serialNumber").GetString());
        Assert.Equal("station-api-trace", body.RootElement.GetProperty("stationId").GetString());
        Assert.Equal("process-api-trace@1.0.0", body.RootElement.GetProperty("processVersionId").GetString());
        Assert.Equal("config-api-trace", body.RootElement.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("measurements").GetArrayLength());
        Assert.Equal(1, body.RootElement.GetProperty("artifacts").GetArrayLength());
        Assert.Equal(1, body.RootElement.GetProperty("auditEntries").GetArrayLength());

        using var queryResponse = await _client.GetAsync($"/api/traceability/records?serialNumber={serialNumber}");
        using var queryBody = await ReadJsonAsync(queryResponse);

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(1, queryBody.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(traceRecordId, queryBody.RootElement.GetProperty("items")[0].GetProperty("traceRecordId").GetGuid());

        using var getResponse = await _client.GetAsync($"/api/traceability/records/{traceRecordId}");
        using var getBody = await ReadJsonAsync(getResponse);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("trace/SMX-API/vision.png", getBody.RootElement.GetProperty("artifacts")[0].GetProperty("storageKey").GetString());

        using var exportResponse = await _client.GetAsync($"/api/traceability/records/{traceRecordId}/export");
        using var exportBody = await ReadJsonAsync(exportResponse);

        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal("openlineops.trace-package.v1", exportBody.RootElement.GetProperty("packageFormatVersion").GetString());
        Assert.Equal(traceRecordId, exportBody.RootElement.GetProperty("traceRecord").GetProperty("traceRecordId").GetGuid());
    }

    [Fact]
    public async Task CreateTraceRecordWithoutJudgementGeneratesFailedFromMeasurementRule()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var serialNumber = $"SMX-JUDGEMENT-{suffix}";

        using var response = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRecordRequestWithoutJudgement(serialNumber, BaseTimeUtc.AddMinutes(4)));
        using var body = await ReadJsonAsync(response);
        var traceRecordId = body.RootElement.GetProperty("traceRecordId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Failed", body.RootElement.GetProperty("judgement").GetString());

        using var queryResponse = await _client.GetAsync($"/api/traceability/records?serialNumber={serialNumber}");
        using var queryBody = await ReadJsonAsync(queryResponse);

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(traceRecordId, queryBody.RootElement.GetProperty("items")[0].GetProperty("traceRecordId").GetGuid());
        Assert.Equal("Failed", queryBody.RootElement.GetProperty("items")[0].GetProperty("judgement").GetString());
    }

    [Fact]
    public async Task QueryTraceRecordsSupportsStablePagination()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var batchId = $"batch-api-page-{suffix}";

        using var third = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRecordRequest($"SMX-PAGE-3-{suffix}", batchId, BaseTimeUtc.AddMinutes(3)));
        using var first = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRecordRequest($"SMX-PAGE-1-{suffix}", batchId, BaseTimeUtc.AddMinutes(1)));
        using var second = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRecordRequest($"SMX-PAGE-2-{suffix}", batchId, BaseTimeUtc.AddMinutes(2)));

        using var pageOneResponse = await _client.GetAsync(
            $"/api/traceability/records?batchId={batchId}&pageNumber=1&pageSize=2");
        using var pageOneBody = await ReadJsonAsync(pageOneResponse);
        using var pageTwoResponse = await _client.GetAsync(
            $"/api/traceability/records?batchId={batchId}&pageNumber=2&pageSize=2");
        using var pageTwoBody = await ReadJsonAsync(pageTwoResponse);

        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pageOneResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pageTwoResponse.StatusCode);
        Assert.Equal(3, pageOneBody.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(2, pageOneBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal($"SMX-PAGE-1-{suffix}", pageOneBody.RootElement.GetProperty("items")[0].GetProperty("serialNumber").GetString());
        Assert.Equal($"SMX-PAGE-2-{suffix}", pageOneBody.RootElement.GetProperty("items")[1].GetProperty("serialNumber").GetString());
        Assert.Equal($"SMX-PAGE-3-{suffix}", pageTwoBody.RootElement.GetProperty("items")[0].GetProperty("serialNumber").GetString());
    }

    [Fact]
    public async Task CreateTraceRecordWithMissingRequiredLinkReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            new
            {
                runtimeSessionId = Guid.NewGuid(),
                serialNumber = "",
                batchId = "batch-invalid",
                stationId = "station-api-trace",
                fixtureId = "fixture-api-trace",
                processDefinitionId = "process-api-trace",
                processVersionId = "process-api-trace@1.0.0",
                configurationSnapshotId = "config-api-trace",
                recipeSnapshotId = "recipe-api-trace@1.0.0",
                deviceId = "vision-camera-api",
                judgement = "Passed",
                startedAtUtc = BaseTimeUtc,
                completedAtUtc = BaseTimeUtc.AddMinutes(1),
                recordedBy = "operator-api"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetMissingTraceRecordReturnsNotFound()
    {
        using var response = await _client.GetAsync($"/api/traceability/records/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static object CreateTraceRecordRequest(
        string serialNumber,
        string batchId,
        DateTimeOffset completedAtUtc)
    {
        return new
        {
            runtimeSessionId = Guid.NewGuid(),
            serialNumber,
            batchId,
            stationId = "station-api-trace",
            fixtureId = "fixture-api-trace",
            processDefinitionId = "process-api-trace",
            processVersionId = "process-api-trace@1.0.0",
            configurationSnapshotId = "config-api-trace",
            recipeSnapshotId = "recipe-api-trace@1.0.0",
            deviceId = "vision-camera-api",
            judgement = "Passed",
            startedAtUtc = BaseTimeUtc,
            completedAtUtc,
            recordedBy = "operator-api",
            measurements = new[]
            {
                new
                {
                    name = "width",
                    numericValue = 12.34m,
                    textValue = (string?)null,
                    unit = "mm",
                    deviceId = "vision-camera-api",
                    runtimeCommandId = Guid.NewGuid(),
                    passed = true,
                    measuredAtUtc = completedAtUtc.AddSeconds(-10)
                }
            },
            artifacts = new[]
            {
                new
                {
                    name = "vision-image",
                    kind = "Image",
                    storageKey = "trace/SMX-API/vision.png",
                    mediaType = "image/png",
                    sizeBytes = 1024,
                    sha256 = "sha256-api",
                    deviceId = "vision-camera-api",
                    capturedAtUtc = completedAtUtc.AddSeconds(-5)
                }
            },
            auditEntries = new[]
            {
                new
                {
                    actorId = "operator-api",
                    action = "TraceRecord.Completed",
                    detail = "API integration test trace.",
                    occurredAtUtc = completedAtUtc
                }
            }
        };
    }

    private static object CreateTraceRecordRequestWithoutJudgement(
        string serialNumber,
        DateTimeOffset completedAtUtc)
    {
        return new
        {
            runtimeSessionId = Guid.NewGuid(),
            serialNumber,
            batchId = "batch-api-judgement",
            stationId = "station-api-trace",
            fixtureId = "fixture-api-trace",
            processDefinitionId = "process-api-trace",
            processVersionId = "process-api-trace@1.0.0",
            configurationSnapshotId = "config-api-trace",
            recipeSnapshotId = "recipe-api-trace@1.0.0",
            deviceId = "vision-camera-api",
            startedAtUtc = BaseTimeUtc,
            completedAtUtc,
            recordedBy = "operator-api",
            measurements = new[]
            {
                new
                {
                    name = "width",
                    numericValue = 12.34m,
                    textValue = (string?)null,
                    unit = "mm",
                    deviceId = "vision-camera-api",
                    runtimeCommandId = Guid.NewGuid(),
                    passed = false,
                    measuredAtUtc = completedAtUtc.AddSeconds(-10)
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
