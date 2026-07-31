using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class StationAgentControlLeaseApiTests :
    IClassFixture<OpenLineOpsApiWebApplicationFactory>,
    IDisposable
{
    private const string InstanceA = "11111111-1111-4111-8111-111111111111";
    private const string InstanceB = "22222222-2222-4222-8222-222222222222";
    private const string LeaseHandle =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly WebApplicationFactory<Program> _factory;

    public StationAgentControlLeaseApiTests(
        OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenLineOps:Runtime:Persistence:Provider"] =
                        RuntimeSessionPersistenceProviders.InMemory,
                    ["OpenLineOps:Runtime:Coordination:Provider"] =
                        ProductionCoordinationPersistenceProviders.InMemory,
                    ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                    ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess",
                    ["OpenLineOps:Runtime:StationAgentControlLease:TimeToLive"] =
                        "00:00:30"
                });
            });
        });
    }

    [Fact]
    public async Task ApiEnforcesLeaseOwnerTokenStationIdentityAndStrictJson()
    {
        var route = Route(ApiTestAuthentication.StationAgentStationId);
        using var operatorClient = Client(ApiTestAuthentication.OperatorToken);
        using var agent = Client(ApiTestAuthentication.StationAgentToken);
        agent.DefaultRequestHeaders.Add(
            "X-OpenLineOps-Agent-Lease",
            LeaseHandle);

        using var wrongRole = await operatorClient.PostAsJsonAsync(
            $"{route}/acquire",
            new { ownerInstanceId = InstanceA });
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);

        using var wrongStation = await agent.PostAsJsonAsync(
            $"{Route("station.other")}/acquire",
            new { ownerInstanceId = InstanceA });
        Assert.Equal(HttpStatusCode.Forbidden, wrongStation.StatusCode);

        using var unmapped = await agent.PostAsJsonAsync(
            $"{route}/acquire",
            new { ownerInstanceId = InstanceA, unexpected = true });
        Assert.Equal(HttpStatusCode.BadRequest, unmapped.StatusCode);

        using var acquired = await agent.PostAsJsonAsync(
            $"{route}/acquire",
            new { ownerInstanceId = InstanceA });
        using var acquiredJson = await ReadJsonAsync(acquired);
        Assert.Equal(HttpStatusCode.OK, acquired.StatusCode);
        Assert.Equal(
            ApiTestAuthentication.StationAgentActorId,
            acquiredJson.RootElement.GetProperty("ownerAgentId").GetString());
        Assert.Equal(
            InstanceA,
            acquiredJson.RootElement.GetProperty("ownerInstanceId").GetString());
        Assert.True(acquiredJson.RootElement.GetProperty("active").GetBoolean());
        var token = acquiredJson.RootElement.GetProperty("fencingToken").GetInt64();
        var originalExpiry = acquiredJson.RootElement
            .GetProperty("expiresAtUtc")
            .GetDateTimeOffset();

        using var acquireRetry = await agent.PostAsJsonAsync(
            $"{route}/acquire",
            new { ownerInstanceId = InstanceA });
        using var retryJson = await ReadJsonAsync(acquireRetry);
        Assert.Equal(HttpStatusCode.OK, acquireRetry.StatusCode);
        Assert.Equal(
            token,
            retryJson.RootElement.GetProperty("fencingToken").GetInt64());
        Assert.Equal(
            originalExpiry,
            retryJson.RootElement.GetProperty("expiresAtUtc").GetDateTimeOffset());

        using var contended = await agent.PostAsJsonAsync(
            $"{route}/acquire",
            new { ownerInstanceId = InstanceB });
        using var contendedJson = await ReadJsonAsync(contended);
        Assert.Equal(HttpStatusCode.Conflict, contended.StatusCode);
        Assert.Equal(
            "Conflict.Runtime.StationAgentControlLeaseHeld",
            contendedJson.RootElement.GetProperty("title").GetString());

        using var staleRenew = await agent.PostAsJsonAsync(
            $"{route}/renew",
            new { ownerInstanceId = InstanceA, fencingToken = token + 1 });
        using var staleRenewJson = await ReadJsonAsync(staleRenew);
        Assert.Equal(HttpStatusCode.Conflict, staleRenew.StatusCode);
        Assert.Equal(
            "Conflict.Runtime.StationAgentControlLeaseStaleFencingToken",
            staleRenewJson.RootElement.GetProperty("title").GetString());

        using var renewed = await agent.PostAsJsonAsync(
            $"{route}/renew",
            new { ownerInstanceId = InstanceA, fencingToken = token });
        using var renewedJson = await ReadJsonAsync(renewed);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(
            token,
            renewedJson.RootElement.GetProperty("fencingToken").GetInt64());
        Assert.True(
            renewedJson.RootElement.GetProperty("expiresAtUtc").GetDateTimeOffset()
            >= originalExpiry);

        using var released = await agent.PostAsJsonAsync(
            $"{route}/release",
            new { ownerInstanceId = InstanceA, fencingToken = token });
        using var releasedJson = await ReadJsonAsync(released);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.False(releasedJson.RootElement.GetProperty("active").GetBoolean());

        using var reacquired = await agent.PostAsJsonAsync(
            $"{route}/acquire",
            new { ownerInstanceId = InstanceB });
        using var reacquiredJson = await ReadJsonAsync(reacquired);
        Assert.Equal(HttpStatusCode.OK, reacquired.StatusCode);
        Assert.True(
            reacquiredJson.RootElement.GetProperty("fencingToken").GetInt64()
            > token);
    }

    [Fact]
    public async Task OwnerInstanceMustBeFreshBootUuidV4()
    {
        using var agent = Client(ApiTestAuthentication.StationAgentToken);
        agent.DefaultRequestHeaders.Add(
            "X-OpenLineOps-Agent-Lease",
            LeaseHandle);
        using var response = await agent.PostAsJsonAsync(
            $"{Route(ApiTestAuthentication.StationAgentStationId)}/acquire",
            new { ownerInstanceId = "configured-machine-id" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient Client(string token) =>
        _factory.CreateAuthenticatedClient(token: token);

    private static string Route(string stationId) =>
        $"/api/stations/{Uri.EscapeDataString(stationId)}/agent-control-lease";

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public void Dispose()
    {
        _factory.Dispose();
    }
}
