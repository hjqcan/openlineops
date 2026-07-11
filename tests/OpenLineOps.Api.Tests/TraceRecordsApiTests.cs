using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class TraceRecordsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private readonly HttpClient _client;

    public TraceRecordsApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task CreateProductionRunTraceReturnsOperationEvidenceAndCanQueryGetAndExport()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unitIdentity = $"SMX-API-{suffix}";
        var productionRunId = Guid.NewGuid();
        using var response = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest(productionRunId, unitIdentity, "lot-api-main", BaseTimeUtc.AddMinutes(3)));
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(productionRunId, body.RootElement.GetProperty("traceRecordId").GetGuid());
        Assert.Equal(productionRunId, body.RootElement.GetProperty("productionRunId").GetGuid());
        Assert.Equal(
            unitIdentity,
            body.RootElement.GetProperty("productionUnitIdentityValue").GetString());
        Assert.Equal("Completed", body.RootElement.GetProperty("executionStatus").GetString());
        Assert.Equal("Completed", body.RootElement.GetProperty("disposition").GetString());
        var operation = body.RootElement.GetProperty("operations")[0];
        Assert.Equal("operation-api-trace", operation.GetProperty("operationId").GetString());
        Assert.Equal("station-system-api-trace", operation.GetProperty("stationSystemId").GetString());
        Assert.Equal(
            "process-api-trace@1.0.0",
            operation.GetProperty("processVersionId").GetString());
        Assert.Equal(1, operation.GetProperty("commands").GetArrayLength());
        Assert.Equal(
            "action.inspect.width",
            operation.GetProperty("commands")[0].GetProperty("actionId").GetString());
        Assert.Equal(
            "Passed",
            operation.GetProperty("commands")[0].GetProperty("resultJudgement").GetString());
        Assert.Equal(1, operation.GetProperty("measurements").GetArrayLength());
        Assert.Equal(1, operation.GetProperty("artifacts").GetArrayLength());
        Assert.Equal(1, operation.GetProperty("outputs").GetArrayLength());
        Assert.Equal(2, operation.GetProperty("fencingTokens").GetArrayLength());

        using var queryResponse = await _client.GetAsync(
            "/api/traceability/records?productionUnitIdentityValue="
            + $"{unitIdentity}&stationSystemId=station-system-api-trace");
        using var queryBody = await ReadJsonAsync(queryResponse);
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(1, queryBody.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            productionRunId,
            queryBody.RootElement.GetProperty("items")[0].GetProperty("productionRunId").GetGuid());

        using var getResponse = await _client.GetAsync($"/api/traceability/records/{productionRunId}");
        using var getBody = await ReadJsonAsync(getResponse);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(
            "trace/SMX-API/vision.png",
            getBody.RootElement.GetProperty("operations")[0]
                .GetProperty("artifacts")[0]
                .GetProperty("storageKey")
                .GetString());

        using var exportResponse = await _client.GetAsync(
            $"/api/traceability/records/{productionRunId}/export");
        using var exportBody = await ReadJsonAsync(exportResponse);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal(
            "openlineops.production-run-trace-package",
            exportBody.RootElement.GetProperty("packageFormat").GetString());
        Assert.Equal(
            productionRunId,
            exportBody.RootElement.GetProperty("traceRecord").GetProperty("productionRunId").GetGuid());
    }

    [Fact]
    public async Task DuplicateProductionRunTraceReturnsConflictAndDoesNotCreateSecondRecord()
    {
        var productionRunId = Guid.NewGuid();
        var request = CreateTraceRequest(
            productionRunId,
            $"UNIT-DUPLICATE-{Guid.NewGuid():N}",
            $"lot-duplicate-{Guid.NewGuid():N}",
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
        var lotId = $"lot-api-page-{suffix}";
        foreach (var (identity, minute) in new[] { ("3", 3), ("1", 1), ("2", 2) })
        {
            using var response = await _client.PostAsJsonAsync(
                "/api/traceability/records",
                CreateTraceRequest(
                    Guid.NewGuid(),
                    $"UNIT-PAGE-{identity}-{suffix}",
                    lotId,
                    BaseTimeUtc.AddMinutes(minute)));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var pageOneResponse = await _client.GetAsync(
            $"/api/traceability/records?lotId={lotId}&pageNumber=1&pageSize=2");
        using var pageOne = await ReadJsonAsync(pageOneResponse);
        using var pageTwoResponse = await _client.GetAsync(
            $"/api/traceability/records?lotId={lotId}&pageNumber=2&pageSize=2");
        using var pageTwo = await ReadJsonAsync(pageTwoResponse);
        Assert.Equal(
            [$"UNIT-PAGE-1-{suffix}", $"UNIT-PAGE-2-{suffix}"],
            pageOne.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("productionUnitIdentityValue").GetString()));
        Assert.Equal(
            $"UNIT-PAGE-3-{suffix}",
            pageTwo.RootElement.GetProperty("items")[0]
                .GetProperty("productionUnitIdentityValue")
                .GetString());
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
                $"UNIT-CASE-{Guid.NewGuid():N}",
                $"lot-case-{Guid.NewGuid():N}",
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
            "/api/traceability/records?productionUnitIdentityValue=%20UNIT-INVALID");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRejectsCaseChangedCommandResultJudgement()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/traceability/records",
            CreateTraceRequest(
                Guid.NewGuid(),
                $"UNIT-JUDGEMENT-{Guid.NewGuid():N}",
                $"lot-judgement-{Guid.NewGuid():N}",
                BaseTimeUtc.AddMinutes(5),
                resultJudgement: "passed"));
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("case-sensitive", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("executionStatus=completed")]
    [InlineData("judgement=passed")]
    [InlineData("disposition=completed")]
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
        string productionUnitIdentityValue,
        string lotId,
        DateTimeOffset completedAtUtc,
        string? runtimeSessionStatus = null,
        string targetKind = "Slot",
        string commandStatus = "Completed",
        string artifactKind = "Image",
        string incidentSeverity = "Error",
        string judgement = "Passed",
        string stationSystemId = "station-system-api-trace",
        string processVersionId = "process-api-trace@1.0.0",
        string? resultJudgement = null)
    {
        var runtimeSessionId = Guid.NewGuid();
        var runtimeCommandId = Guid.NewGuid();
        var productionUnitId = Guid.NewGuid();
        var executionFailed = string.Equals(commandStatus, "Failed", StringComparison.Ordinal);
        var executionStatus = executionFailed ? "Failed" : "Completed";
        var operationJudgement = executionFailed ? "Unknown" : judgement;
        var rootJudgement = executionFailed ? "Unknown" : judgement;
        return new
        {
            productionRunId,
            productionUnitId,
            projectId = "project-api-trace",
            applicationId = "application-api-trace",
            projectSnapshotId = "snapshot-api-trace",
            topologyId = "topology-api-trace",
            productionLineDefinitionId = "line-api-trace",
            productModelId = "product-model-api",
            productionUnitIdentityInputKey = "serialNumber",
            productionUnitIdentityValue,
            lotId,
            carrierId = "carrier-api-trace",
            actorId = "operator-api",
            executionStatus,
            judgement = rootJudgement,
            disposition = executionFailed
                ? "Held"
                : judgement switch
                {
                    "Failed" => "Nonconforming",
                    "Aborted" => "Held",
                    _ => "Completed"
                },
            createdAtUtc = BaseTimeUtc,
            startedAtUtc = BaseTimeUtc,
            completedAtUtc,
            failureCode = executionFailed ? "Runtime.OperationFailed" : null,
            failureReason = executionFailed ? "Inspection execution failed." : null,
            operations = new[]
            {
                new
                {
                    operationRunId = "operation-api-trace@0001",
                    operationId = "operation-api-trace",
                    attempt = 1,
                    stationSystemId,
                    stationId = "station-api-trace",
                    processDefinitionId = "process-api-trace",
                    processVersionId,
                    configurationSnapshotId = "config-api-trace",
                    recipeSnapshotId = "recipe-api-trace@1.0.0",
                    runtimeSessionId,
                    runtimeSessionStatus = runtimeSessionStatus ?? executionStatus,
                    executionStatus,
                    judgement = operationJudgement,
                    startedAtUtc = BaseTimeUtc,
                    completedAtUtc,
                    failureCode = executionFailed ? "Runtime.CommandFailed" : null,
                    failureReason = executionFailed ? "Inspection execution failed." : null,
                    completedStepCount = executionFailed ? 0 : 1,
                    commandCount = 1,
                    incidentCount = executionFailed ? 1 : 0,
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
                            resultJudgement = resultJudgement ?? commandStatus switch
                            {
                                "Completed" => judgement,
                                "Failed" or "TimedOut" or "Canceled" or "Rejected" => "Unknown",
                                _ => null
                            },
                            createdAtUtc = completedAtUtc.AddSeconds(-20),
                            deadlineAtUtc = completedAtUtc.AddSeconds(10),
                            acceptedAtUtc = completedAtUtc.AddSeconds(-19),
                            startedAtUtc = completedAtUtc.AddSeconds(-18),
                            completedAtUtc = completedAtUtc.AddSeconds(-10),
                            resultPayload = executionFailed ? null : "ok",
                            failureReason = executionFailed ? "Inspection execution failed." : null
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
                            passed = executionFailed ? (bool?)null : judgement == "Passed",
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
                    incidents = executionFailed
                        ? new[]
                        {
                            new
                            {
                                runtimeIncidentId = Guid.NewGuid(),
                                severity = incidentSeverity,
                                code = "Runtime.CommandFailed",
                                message = "Inspection execution failed.",
                                occurredAtUtc = completedAtUtc.AddSeconds(-9)
                            }
                        }
                        : [],
                    outputs = executionFailed
                        ? []
                        : new[]
                        {
                            new
                            {
                                key = "inspection.width",
                                valueKind = "FixedPoint",
                                canonicalJson = "12.34"
                            }
                        },
                    fencingTokens = new[]
                    {
                        new
                        {
                            resourceKind = "Station",
                            resourceId = stationSystemId,
                            fencingToken = 1
                        },
                        new
                        {
                            resourceKind = "Device",
                            resourceId = "vision-camera-api",
                            fencingToken = 2
                        }
                    }
                }
            },
            routeDecisions = Array.Empty<object>(),
            auditEntries = new[]
            {
                new
                {
                    actorId = "operator-api",
                    action = executionFailed ? "ProductionRun.Failed" : "ProductionRun.Completed",
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
