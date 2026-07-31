using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class PlatformApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private readonly HttpClient _client;

    public PlatformApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task HealthLiveReturnsMinimalNoContent()
    {
        using var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task HealthReadyReturnsOkForDefaultLocalProfile()
    {
        using var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PlatformEndpointReturnsPlatformIdentity()
    {
        using var response = await _client.GetAsync("/api/platform");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("OpenLineOps", body, StringComparison.Ordinal);
        Assert.Contains("OpenLineOps.Api", body, StringComparison.Ordinal);
    }
}
