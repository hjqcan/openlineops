using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class RemovedLegacyApiSurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public RemovedLegacyApiSurfaceTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Theory]
    [InlineData("/api/process-definitions")]
    [InlineData("/api/process-blocks")]
    [InlineData("/api/engineering/workspaces")]
    [InlineData("/api/engineering/projects")]
    [InlineData("/api/engineering/recipes")]
    [InlineData("/api/engineering/station-profiles")]
    public async Task GlobalProcessAuthoringRoutesDoNotExist(string route)
    {
        using var response = await _client.GetAsync(route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SimulatedRuntimeStartMethodDoesNotExist()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/runtime/sessions/simulated",
            new { });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task ProcessDefinitionRuntimeStartRouteDoesNotExist()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/process-definitions/obsolete/runtime-sessions",
            new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProjectSnapshotRuntimeSessionStartRouteDoesNotExist()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/automation-projects/obsolete/snapshots/obsolete/runtime-sessions",
            new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProductionRunStartRejectsRemovedSerialNumberField()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/automation-projects/obsolete/snapshots/obsolete/production-runs",
            new
            {
                productionRunId = Guid.NewGuid(),
                dutIdentityValue = "DUT-001",
                actorId = "operator-a",
                serialNumber = "SN-001"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
