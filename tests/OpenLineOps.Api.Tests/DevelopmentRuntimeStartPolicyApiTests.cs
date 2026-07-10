using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Tests;

public sealed class DevelopmentRuntimeStartPolicyApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DevelopmentRuntimeStartPolicyApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/runtime/sessions/simulated")]
    [InlineData("/api/process-definitions/missing-process/runtime-sessions")]
    public async Task CompatibilityRuntimeStartEndpointsAreForbiddenByDefault(string endpoint)
    {
        using var client = CreateClient(_factory);

        using var response = await client.PostAsJsonAsync(endpoint, new { });

        await AssertDisabledProblemAsync(response);
    }

    [Theory]
    [InlineData("/api/runtime/sessions/simulated")]
    [InlineData("/api/process-definitions/missing-process/runtime-sessions")]
    public async Task ExplicitFlagCannotEnableCompatibilityRuntimeStartsInProduction(string endpoint)
    {
        using var productionFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DevelopmentRuntimeStartPolicy.EnabledConfigurationKey] = "true"
                });
            });
        });
        using var client = CreateClient(productionFactory);

        using var response = await client.PostAsJsonAsync(endpoint, new { });

        await AssertDisabledProblemAsync(response);
    }

    [Fact]
    public async Task ProjectSnapshotRuntimeStartRouteIsNotSubjectToDevelopmentStartGate()
    {
        using var client = CreateClient(_factory);

        using var response = await client.PostAsJsonAsync(
            "/api/automation-projects/missing-project/snapshots/missing-snapshot/runtime-sessions",
            new { });
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NotFound.Projects.ProjectNotFound", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task RuntimeReadAndMonitoringEndpointsRemainAvailableByDefault()
    {
        using var client = CreateClient(_factory);

        using var recoveryResponse = await client.GetAsync("/api/runtime/sessions/recovery-plan");
        using var stationsResponse = await client.GetAsync("/api/runtime/monitoring/stations");

        Assert.Equal(HttpStatusCode.OK, recoveryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stationsResponse.StatusCode);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private static async Task AssertDisabledProblemAsync(HttpResponseMessage response)
    {
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(403, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            DevelopmentRuntimeStartPolicy.DisabledErrorCode,
            body.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            DevelopmentRuntimeStartPolicy.DisabledErrorMessage,
            body.RootElement.GetProperty("detail").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
