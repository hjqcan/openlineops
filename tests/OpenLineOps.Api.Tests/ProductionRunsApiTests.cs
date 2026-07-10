using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
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
    public async Task GetByIdReturnsCompleteProductionRunAndRejectsNonCanonicalIdentity()
    {
        var runId = new ProductionRunId(Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab"));
        var createdAtUtc = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        var run = ProductionRun.Create(
            runId,
            "project.production-run-api",
            "application.production-run-api",
            "snapshot.production-run-api",
            "topology.production-run-api",
            "line.production-run-api",
            new DutIdentity("dut.production-run-api", "serialNumber", "DUT-API-001"),
            "batch.production-run-api",
            "fixture.production-run-api",
            "device.production-run-api",
            "operator.production-run-api",
            createdAtUtc,
            [
                new ProductionStageRunDefinition(
                    "stage.production-run-api",
                    1,
                    "workstation.production-run-api",
                    new StationId("station.production-run-api"),
                    new ProcessDefinitionId("process.production-run-api"),
                    new ProcessVersionId("process.production-run-api@1.0.0"),
                    new ConfigurationSnapshotId("configuration.production-run-api"),
                    new RecipeSnapshotId("recipe.production-run-api"))
            ]);
        var repository = _factory.Services.GetRequiredService<IProductionRunRepository>();
        Assert.True(await repository.TryAddAsync(run));
        Assert.True(run.Start(createdAtUtc.AddSeconds(1)).Succeeded);
        var sessionId = RuntimeSessionId.New();
        Assert.True(run.StartStage(
            "stage.production-run-api",
            sessionId,
            createdAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(run.CompleteStage(
            "stage.production-run-api",
            completedStepCount: 3,
            commandCount: 4,
            incidentCount: 1,
            createdAtUtc.AddSeconds(3)).Succeeded);

        await repository.SaveAsync(run, 0);

        using var response = await _client.GetAsync($"/api/runtime/production-runs/{runId.Value:D}");
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = document.RootElement;
        Assert.Equal(runId.Value, root.GetProperty("productionRunId").GetGuid());
        Assert.Equal("project.production-run-api", root.GetProperty("projectId").GetString());
        Assert.Equal("application.production-run-api", root.GetProperty("applicationId").GetString());
        Assert.Equal("snapshot.production-run-api", root.GetProperty("projectSnapshotId").GetString());
        Assert.Equal("topology.production-run-api", root.GetProperty("topologyId").GetString());
        Assert.Equal("line.production-run-api", root.GetProperty("productionLineDefinitionId").GetString());
        Assert.Equal("operator.production-run-api", root.GetProperty("actorId").GetString());
        Assert.Equal("batch.production-run-api", root.GetProperty("batchId").GetString());
        Assert.Equal("fixture.production-run-api", root.GetProperty("fixtureId").GetString());
        Assert.Equal("device.production-run-api", root.GetProperty("deviceId").GetString());
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("isTerminal").GetBoolean());
        Assert.Equal(1, root.GetProperty("completedStageCount").GetInt32());
        Assert.Equal(3, root.GetProperty("completedStepCount").GetInt32());
        Assert.Equal(4, root.GetProperty("commandCount").GetInt32());
        Assert.Equal(1, root.GetProperty("incidentCount").GetInt32());
        var dutIdentity = root.GetProperty("dutIdentity");
        Assert.Equal("dut.production-run-api", dutIdentity.GetProperty("modelId").GetString());
        Assert.Equal("serialNumber", dutIdentity.GetProperty("inputKey").GetString());
        Assert.Equal("DUT-API-001", dutIdentity.GetProperty("value").GetString());
        var stage = Assert.Single(root.GetProperty("stages").EnumerateArray());
        Assert.Equal("stage.production-run-api", stage.GetProperty("stageId").GetString());
        Assert.Equal("workstation.production-run-api", stage.GetProperty("workstationId").GetString());
        Assert.Equal("station.production-run-api", stage.GetProperty("stationSystemId").GetString());
        Assert.Equal(sessionId.Value, stage.GetProperty("runtimeSessionId").GetGuid());
        Assert.Equal(3, stage.GetProperty("completedStepCount").GetInt32());
        Assert.Equal(4, stage.GetProperty("commandCount").GetInt32());
        Assert.Equal(1, stage.GetProperty("incidentCount").GetInt32());
        Assert.True(stage.GetProperty("isTerminal").GetBoolean());

        using var nonCanonicalResponse = await _client.GetAsync(
            $"/api/runtime/production-runs/{runId.Value.ToString("D").ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.BadRequest, nonCanonicalResponse.StatusCode);

        using var missingResponse = await _client.GetAsync(
            $"/api/runtime/production-runs/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
