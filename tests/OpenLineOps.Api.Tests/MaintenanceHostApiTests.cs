using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class MaintenanceHostApiTests(
    OpenLineOpsApiWebApplicationFactory factory)
    : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    [Fact]
    public async Task HostRegistersMaintenanceApiAndEvaluatesPersistedStartBlocks()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var assetId = $"fixture-{suffix}";
        using var engineering = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);
        using var operatorClient = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/operations/maintenance/assets")
        {
            Content = JsonContent.Create(new
            {
                assetId,
                stationId = ApiTestAuthentication.StationAgentStationId,
                displayName = "Functional test fixture",
                productionCritical = true,
                requiresCalibration = true,
                occurredAtUtc = DateTimeOffset.UtcNow
            })
        };
        request.Headers.Add("Idempotency-Key", $"register-{suffix}");
        using var created = await engineering.SendAsync(request);
        using var createdDocument = await JsonDocument.ParseAsync(
            await created.Content.ReadAsStreamAsync());
        using var readiness = await operatorClient.GetAsync(
            "/api/operations/maintenance/stations/"
            + $"{ApiTestAuthentication.StationAgentStationId}/production-readiness");
        using var readinessDocument = await JsonDocument.ParseAsync(
            await readiness.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(
            assetId,
            createdDocument.RootElement.GetProperty("assetId").GetString());
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.False(readinessDocument.RootElement.GetProperty("allowed").GetBoolean());
        Assert.NotEmpty(
            readinessDocument.RootElement.GetProperty("blocks").EnumerateArray());
    }

    [Fact]
    public async Task HostBindsStationAgentWritesAndRejectsSpoofedMembers()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var assetId = $"instrument-{suffix}";
        using var engineering = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);
        using var agent = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.StationAgentToken);

        using (var register = new HttpRequestMessage(
                   HttpMethod.Post,
                   "/api/operations/maintenance/assets")
               {
                   Content = JsonContent.Create(new
                   {
                       assetId,
                       stationId = ApiTestAuthentication.StationAgentStationId,
                       displayName = "Measurement instrument",
                       productionCritical = true,
                       requiresCalibration = false,
                       occurredAtUtc = DateTimeOffset.UtcNow
                   })
               })
        {
            register.Headers.Add("Idempotency-Key", $"register-{suffix}");
            using var registered = await engineering.SendAsync(register);
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }

        using (var health = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/api/operations/maintenance/assets/{assetId}/health")
               {
                   Content = JsonContent.Create(new
                   {
                       status = "Healthy",
                       diagnostic = "self-test passed",
                       occurredAtUtc = DateTimeOffset.UtcNow
                   })
               })
        {
            health.Headers.Add("Idempotency-Key", $"health-{suffix}");
            using var recorded = await agent.SendAsync(health);
            Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        }

        using var spoofed = await engineering.PostAsJsonAsync(
            "/api/operations/maintenance/assets",
            new
            {
                assetId = $"spoofed-{suffix}",
                stationId = ApiTestAuthentication.StationAgentStationId,
                displayName = "Spoofed asset",
                productionCritical = false,
                requiresCalibration = false,
                occurredAtUtc = DateTimeOffset.UtcNow,
                actorId = "spoofed-actor"
            });
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);
    }

    [Fact]
    public void HostCompositionInstallsMaintenanceAwareProductionReadiness()
    {
        using var scope = factory.Services.CreateScope();

        var readiness =
            scope.ServiceProvider.GetRequiredService<IProductionOperationReadiness>();

        Assert.Equal(
            "OpenLineOps.Api.Integrations."
            + "RecipeAwareProductionOperationReadiness",
            readiness.GetType().FullName);
    }
}
