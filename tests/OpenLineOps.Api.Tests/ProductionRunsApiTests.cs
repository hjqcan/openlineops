using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionRunsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ProductionRunsApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task PostCreatesAsynchronouslyAndReturnsCanonicalLocation()
    {
        var runId = Guid.NewGuid();
        var request = new
        {
            productionRunId = runId,
            projectId = "project.api-submit",
            applicationId = "application.api-submit",
            projectSnapshotId = "snapshot.api-submit",
            topologyId = "topology.api-submit",
            productionLineDefinitionId = "line.api-submit",
            productionUnitIdentity = new
            {
                modelId = "product.board",
                inputKey = "serialNumber",
                value = "SN-API-001"
            },
            actorId = "operator.api",
            entryOperationId = "operation.main",
            operations = new[]
            {
                new
                {
                    operationId = "operation.main",
                    stationSystemId = "station.main",
                    runtimeStationId = "station.main",
                    configurationSnapshotId = "configuration.main",
                    recipeSnapshotId = "recipe.main",
                    processDefinitionId = "process.main",
                    processVersionId = "process-version.main",
                    resources = new[] { new { kind = "Station", resourceId = "station.main" } },
                    process = new
                    {
                        startNodeId = (string?)null,
                        nodes = Array.Empty<object>(),
                        routingNodes = Array.Empty<object>(),
                        transitions = Array.Empty<object>()
                    }
                }
            },
            routeTransitions = Array.Empty<object>(),
            lotId = "lot-001",
            carrierId = "carrier-001"
        };

        using var response = await _client.PostAsJsonAsync("/api/production-runs", request);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/production-runs/{runId:D}", response.Headers.Location?.AbsolutePath);
        Assert.Equal(runId, document.RootElement.GetProperty("productionRunId").GetGuid());
        Assert.Equal("Pending", document.RootElement.GetProperty("executionStatus").GetString());
        Assert.Equal("InProcess", document.RootElement.GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task GetByIdReturnsOperationDualAxesAndRejectsNonCanonicalIdentity()
    {
        var runId = ProductionRunId.New();
        var createdAtUtc = new DateTimeOffset(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);
        var operationPlan = OperationPlan();
        var run = ProductionRun.Create(
            runId,
            "project.production-run-api",
            "application.production-run-api",
            "snapshot.production-run-api",
            "topology.production-run-api",
            "line.production-run-api",
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-API-001"),
            "lot.production-run-api",
            "carrier.production-run-api",
            "operator.production-run-api",
            operationPlan.Definition.OperationId,
            createdAtUtc,
            [operationPlan.Definition],
            []);
        var repository = _factory.Services.GetRequiredService<IProductionRunRepository>();
        Assert.True(await repository.TryAddAsync(
            run,
            new ProductionRunExecutionPlan(runId, [operationPlan])));
        Assert.True(run.Start(createdAtUtc.AddSeconds(1)).Succeeded);
        var operation = Assert.Single(run.Operations);
        var leases = operation.ResourceRequirements.Select(resource => new ResourceLease(
            resource,
            run.Id,
            operation.OperationRunId,
            7,
            createdAtUtc,
            createdAtUtc.AddHours(1))).ToArray();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            createdAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Failed,
            null,
            3,
            4,
            1,
            createdAtUtc.AddSeconds(3)).Succeeded);
        await repository.SaveAsync(run, 0);

        using var response = await _client.GetAsync($"/api/production-runs/{runId.Value:D}");
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = document.RootElement;
        Assert.Equal("Completed", root.GetProperty("executionStatus").GetString());
        Assert.Equal("Failed", root.GetProperty("judgement").GetString());
        Assert.Equal("Nonconforming", root.GetProperty("disposition").GetString());
        Assert.Equal("SN-API-001", root.GetProperty("productionUnitIdentity")
            .GetProperty("value").GetString());
        var operationResponse = Assert.Single(root.GetProperty("operations").EnumerateArray());
        Assert.Equal("operation.main", operationResponse.GetProperty("operationId").GetString());
        Assert.Equal("Completed", operationResponse.GetProperty("executionStatus").GetString());
        Assert.Equal("Failed", operationResponse.GetProperty("judgement").GetString());
        Assert.Equal(7, Assert.Single(operationResponse.GetProperty("resources").EnumerateArray())
            .GetProperty("fencingToken").GetInt64());

        using var nonCanonical = await _client.GetAsync(
            $"/api/production-runs/{runId.Value.ToString("D").ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.BadRequest, nonCanonical.StatusCode);
    }

    private static OperationExecutionPlan OperationPlan() => new(
        "operation.main",
        "station.main",
        new StationId("station.main"),
        new ConfigurationSnapshotId("configuration.main"),
        new RecipeSnapshotId("recipe.main"),
        new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.main"),
            new ProcessVersionId("process-version.main"),
            []));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
}
