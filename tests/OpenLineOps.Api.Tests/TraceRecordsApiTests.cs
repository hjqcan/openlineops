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
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task CreateProductionRunTraceReturnsNestedEvidenceAndCanQueryGetAndExport()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var dutIdentity = $"SMX-API-{suffix}";
        var productionRunId = Guid.NewGuid();
        using var response = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest(productionRunId, dutIdentity, "batch-api-main", BaseTimeUtc.AddMinutes(3)));
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(productionRunId, body.RootElement.GetProperty("traceRecordId").GetGuid());
        Assert.Equal(productionRunId, body.RootElement.GetProperty("productionRunId").GetGuid());
        Assert.Equal(dutIdentity, body.RootElement.GetProperty("dutIdentityValue").GetString());
        Assert.Equal("Completed", body.RootElement.GetProperty("runStatus").GetString());
        var stage = body.RootElement.GetProperty("stages")[0];
        Assert.Equal("stage-api-trace", stage.GetProperty("stageId").GetString());
        Assert.Equal("station-api-trace", stage.GetProperty("stationId").GetString());
        Assert.Equal("process-api-trace@1.0.0", stage.GetProperty("processVersionId").GetString());
        Assert.Equal(1, stage.GetProperty("commands").GetArrayLength());
        Assert.Equal("action.inspect.width", stage.GetProperty("commands")[0].GetProperty("actionId").GetString());
        Assert.Equal("Passed", stage.GetProperty("commands")[0].GetProperty("semanticOutcome").GetString());
        Assert.Equal(1, stage.GetProperty("measurements").GetArrayLength());
        Assert.Equal(1, stage.GetProperty("artifacts").GetArrayLength());

        using var queryResponse = await _client.GetAsync(
            $"/api/traceability/records?dutIdentityValue={dutIdentity}&stationId=station-api-trace");
        using var queryBody = await ReadJsonAsync(queryResponse);
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(1, queryBody.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(productionRunId, queryBody.RootElement.GetProperty("items")[0]
            .GetProperty("productionRunId").GetGuid());

        using var getResponse = await _client.GetAsync($"/api/traceability/records/{productionRunId}");
        using var getBody = await ReadJsonAsync(getResponse);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("trace/SMX-API/vision.png", getBody.RootElement.GetProperty("stages")[0]
            .GetProperty("artifacts")[0].GetProperty("storageKey").GetString());

        using var exportResponse = await _client.GetAsync(
            $"/api/traceability/records/{productionRunId}/export");
        using var exportBody = await ReadJsonAsync(exportResponse);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal("openlineops.production-run-trace-package.v1",
            exportBody.RootElement.GetProperty("packageFormatVersion").GetString());
        Assert.Equal(productionRunId, exportBody.RootElement.GetProperty("traceRecord")
            .GetProperty("productionRunId").GetGuid());
    }

    [Fact]
    public async Task DuplicateProductionRunTraceReturnsConflictAndDoesNotCreateSecondRecord()
    {
        var productionRunId = Guid.NewGuid();
        var request = CreateTraceRequest(
            productionRunId,
            $"SMX-DUPLICATE-{Guid.NewGuid():N}",
            $"batch-duplicate-{Guid.NewGuid():N}",
            BaseTimeUtc.AddMinutes(4));
        using var first = await _client.PostAsJsonAsync("/api/traceability/records", request);
        using var duplicate = await _client.PostAsJsonAsync("/api/traceability/records", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task QueryProductionRunTracesUsesStablePagination()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var batchId = $"batch-api-page-{suffix}";
        foreach (var (identity, minute) in new[] { ("3", 3), ("1", 1), ("2", 2) })
        {
            using var response = await _client.PostAsJsonAsync(
                "/api/traceability/records",
                CreateTraceRequest(
                    Guid.NewGuid(),
                    $"SMX-PAGE-{identity}-{suffix}",
                    batchId,
                    BaseTimeUtc.AddMinutes(minute)));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var pageOneResponse = await _client.GetAsync(
            $"/api/traceability/records?batchId={batchId}&pageNumber=1&pageSize=2");
        using var pageOne = await ReadJsonAsync(pageOneResponse);
        using var pageTwoResponse = await _client.GetAsync(
            $"/api/traceability/records?batchId={batchId}&pageNumber=2&pageSize=2");
        using var pageTwo = await ReadJsonAsync(pageTwoResponse);
        Assert.Equal([$"SMX-PAGE-1-{suffix}", $"SMX-PAGE-2-{suffix}"],
            pageOne.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("dutIdentityValue").GetString()));
        Assert.Equal($"SMX-PAGE-3-{suffix}", pageTwo.RootElement.GetProperty("items")[0]
            .GetProperty("dutIdentityValue").GetString());
    }

    [Theory]
    [InlineData("completed", "Slot", "Completed", "Image", "Error")]
    [InlineData("Completed", "slot", "Completed", "Image", "Error")]
    [InlineData("Completed", "Slot", "completed", "Image", "Error")]
    [InlineData("Completed", "Slot", "Completed", "image", "Error")]
    [InlineData("Failed", "Slot", "Failed", "Image", "error")]
    public async Task CreateRejectsCaseChangedNestedRuntimeEvidenceTokens(
        string runtimeSessionStatus,
        string targetKind,
        string commandStatus,
        string artifactKind,
        string incidentSeverity)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-CASE-{Guid.NewGuid():N}",
                $"batch-case-{Guid.NewGuid():N}",
                BaseTimeUtc.AddMinutes(5),
                runtimeSessionStatus: runtimeSessionStatus,
                targetKind: targetKind,
                commandStatus: commandStatus,
                artifactKind: artifactKind,
                incidentSeverity: incidentSeverity));
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("case-sensitive", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryRejectsPaddedFilterInsteadOfNormalizingIt()
    {
        using var response = await _client.GetAsync(
            "/api/traceability/records?dutIdentityValue=%20SMX-INVALID");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRejectsCaseChangedCommandSemanticOutcome()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest(
                Guid.NewGuid(),
                $"SMX-SEMANTIC-{Guid.NewGuid():N}",
                $"batch-semantic-{Guid.NewGuid():N}",
                BaseTimeUtc.AddMinutes(5),
                semanticOutcome: "passed"));
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("case-sensitive", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("runStatus=completed")]
    [InlineData("judgement=passed")]
    public async Task QueryRejectsCaseChangedEnumFilters(string query)
    {
        using var response = await _client.GetAsync($"/api/traceability/records?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetMissingTraceRecordReturnsNotFound()
    {
        using var response = await _client.GetAsync($"/api/traceability/records/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    internal static object CreateTraceRequest(
        Guid productionRunId,
        string dutIdentityValue,
        string batchId,
        DateTimeOffset completedAtUtc,
        string runtimeSessionStatus = "Completed",
        string targetKind = "Slot",
        string commandStatus = "Completed",
        string artifactKind = "Image",
        string incidentSeverity = "Error",
        string judgement = "Passed",
        string stationId = "station-api-trace",
        string processVersionId = "process-api-trace@1.0.0",
        string? semanticOutcome = null)
    {
        var runtimeSessionId = Guid.NewGuid();
        var runtimeCommandId = Guid.NewGuid();
        var failed = string.Equals(commandStatus, "Failed", StringComparison.Ordinal)
            || string.Equals(judgement, "Failed", StringComparison.Ordinal);
        return new
        {
            productionRunId,
            projectId = "project-api-trace",
            applicationId = "application-api-trace",
            projectSnapshotId = "snapshot-api-trace",
            topologyId = "topology-api-trace",
            productionLineDefinitionId = "line-api-trace",
            dutModelId = "dut-model-api",
            dutIdentityInputKey = "serialNumber",
            dutIdentityValue,
            batchId,
            fixtureId = "fixture-api-trace",
            deviceId = "vision-camera-api",
            actorId = "operator-api",
            runStatus = failed ? "Failed" : "Completed",
            judgement,
            createdAtUtc = BaseTimeUtc,
            startedAtUtc = BaseTimeUtc,
            completedAtUtc,
            failureCode = failed ? "Runtime.StageFailed" : null,
            failureReason = failed ? "Inspection failed." : null,
            stages = new[]
            {
                new
                {
                    stageId = "stage-api-trace",
                    sequence = 1,
                    workstationId = "workstation-api-trace",
                    stationId,
                    processDefinitionId = "process-api-trace",
                    processVersionId,
                    configurationSnapshotId = "config-api-trace",
                    recipeSnapshotId = "recipe-api-trace@1.0.0",
                    runtimeSessionId,
                    runtimeSessionStatus,
                    status = failed ? "Failed" : "Completed",
                    startedAtUtc = BaseTimeUtc,
                    completedAtUtc,
                    failureCode = failed ? "Runtime.CommandFailed" : null,
                    failureReason = failed ? "Inspection failed." : null,
                    completedStepCount = failed ? 0 : 1,
                    commandCount = 1,
                    incidentCount = failed ? 1 : 0,
                    commands = new[]
                    {
                        new
                        {
                            runtimeCommandId,
                            runtimeStepId = Guid.NewGuid(),
                            actionId = "action.inspect.width",
                            targetKind,
                            targetId = "slot.fixture-api.1",
                            targetCapabilityId = "capability.inspect",
                            commandName = "Inspect",
                            status = commandStatus,
                            semanticOutcome = semanticOutcome ?? commandStatus switch
                            {
                                "Completed" => "Passed",
                                "Failed" => "Failed",
                                "Canceled" => "Aborted",
                                _ => null
                            },
                            createdAtUtc = completedAtUtc.AddSeconds(-20),
                            deadlineAtUtc = completedAtUtc.AddSeconds(10),
                            acceptedAtUtc = completedAtUtc.AddSeconds(-19),
                            startedAtUtc = completedAtUtc.AddSeconds(-18),
                            completedAtUtc = completedAtUtc.AddSeconds(-10),
                            resultPayload = failed ? null : "ok",
                            failureReason = failed ? "Inspection failed." : null
                        }
                    },
                    measurements = new[]
                    {
                        new
                        {
                            measurementRecordId = runtimeCommandId,
                            name = "width",
                            numericValue = 12.34m,
                            textValue = (string?)null,
                            unit = "mm",
                            deviceId = "vision-camera-api",
                            runtimeCommandId,
                            actionId = "action.inspect.width",
                            targetKind,
                            targetId = "slot.fixture-api.1",
                            commandStatus,
                            passed = !failed,
                            measuredAtUtc = completedAtUtc.AddSeconds(-10)
                        }
                    },
                    artifacts = new[]
                    {
                        new
                        {
                            name = "vision-image",
                            kind = artifactKind,
                            storageKey = "trace/SMX-API/vision.png",
                            mediaType = "image/png",
                            sizeBytes = 1024,
                            sha256 = new string('a', 64),
                            deviceId = "vision-camera-api",
                            capturedAtUtc = completedAtUtc.AddSeconds(-5)
                        }
                    },
                    incidents = failed
                        ? new[]
                        {
                            new
                            {
                                runtimeIncidentId = Guid.NewGuid(),
                                severity = incidentSeverity,
                                code = "Runtime.CommandFailed",
                                message = "Inspection failed.",
                                occurredAtUtc = completedAtUtc.AddSeconds(-9)
                            }
                        }
                        : []
                }
            },
            auditEntries = new[]
            {
                new
                {
                    actorId = "operator-api",
                    action = failed ? "ProductionRun.Failed" : "ProductionRun.Completed",
                    detail = "API integration test trace.",
                    occurredAtUtc = completedAtUtc
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
