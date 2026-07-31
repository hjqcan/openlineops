using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenLineOps.Api.Tests;

public sealed class IntegrationHostApiTests(
    OpenLineOpsApiWebApplicationFactory factory)
    : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    [Fact]
    public async Task HostRegistersIntegrationApiAndBindsAuthenticatedActor()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workOrderId = $"order-{suffix}";
        using var engineering = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);
        using var operatorClient = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);

        using var created = await engineering.PostAsJsonAsync(
            "/api/integration/work-orders",
            new
            {
                workOrderId,
                productModelId = "model-a",
                targetQuantity = 4,
                factId = $"fact-{suffix}",
                occurredAtUtc = DateTimeOffset.UtcNow
            });
        using var createdDocument = await JsonDocument.ParseAsync(
            await created.Content.ReadAsStreamAsync());
        using var engineeringRead = await engineering.GetAsync(
            $"/api/integration/work-orders/{workOrderId}");
        using var operatorRead = await operatorClient.GetAsync(
            $"/api/integration/work-orders/{workOrderId}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(
            ApiTestAuthentication.EngineeringActorId,
            createdDocument.RootElement
                .GetProperty("facts")[0]
                .GetProperty("actorId")
                .GetString());
        Assert.Equal(HttpStatusCode.Forbidden, engineeringRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorRead.StatusCode);
    }

    [Fact]
    public async Task HostFailsClosedWhenEnterpriseHandlerIsUnconfigured()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var agent = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.StationAgentToken);

        using var response = await agent.PostAsJsonAsync(
            "/api/integration/work-requests",
            new
            {
                workRequestId = $"request-{suffix}",
                workOrderId = $"order-{suffix}",
                kind = "Start",
                stationId = ApiTestAuthentication.StationAgentStationId,
                occurredAtUtc = DateTimeOffset.UtcNow,
                payload = new { quantity = 1 }
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task IntegrationHostRejectsUnknownRequestMembers()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var engineering = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);

        using var response = await engineering.PostAsJsonAsync(
            "/api/integration/work-orders",
            new
            {
                workOrderId = $"order-{suffix}",
                productModelId = "model-a",
                targetQuantity = 1,
                factId = $"fact-{suffix}",
                occurredAtUtc = DateTimeOffset.UtcNow,
                actorId = "spoofed-actor"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
