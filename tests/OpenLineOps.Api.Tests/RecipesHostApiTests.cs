using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Api.Integrations;
using OpenLineOps.Recipes.Application.Fencing;
using OpenLineOps.Recipes.Application.Readiness;

namespace OpenLineOps.Api.Tests;

public sealed class RecipesHostApiTests(
    OpenLineOpsApiWebApplicationFactory factory)
    : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    [Fact]
    public async Task HostExposesFailClosedRecipeReadinessAndProtectsEngineeringRoutes()
    {
        using var standard = factory.CreateAuthenticatedClient();
        using var operatorClient = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);
        using var stationAgent = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.StationAgentToken);
        var evaluatedAt = Uri.EscapeDataString(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var readinessUri =
            "/api/recipes/recipe-unconfigured/verification"
            + "?stationId="
            + Uri.EscapeDataString(ApiTestAuthentication.StationAgentStationId)
            + "&productModelId=product-a"
            + "&versionId=v1"
            + $"&evaluatedAtUtc={evaluatedAt}";

        using var readiness = await standard.GetAsync(readinessUri);
        using var readinessDocument = await JsonDocument.ParseAsync(
            await readiness.Content.ReadAsStreamAsync());
        using var stationScopedDenial = await operatorClient.GetAsync(readinessUri);
        using var engineeringDenial = await stationAgent.GetAsync(
            "/api/recipes/recipe-unconfigured/revisions/v1");

        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.False(
            readinessDocument.RootElement.GetProperty("allowed").GetBoolean());
        Assert.NotEmpty(
            readinessDocument.RootElement.GetProperty("blocks").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, stationScopedDenial.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, engineeringDenial.StatusCode);
    }

    [Fact]
    public void HostCompositionUsesRuntimeLeaseFencingForRecipes()
    {
        using var scope = factory.Services.CreateScope();

        var fencing = scope.ServiceProvider
            .GetRequiredService<IStationFencingTokenValidator>();
        var readiness = scope.ServiceProvider
            .GetRequiredService<IRecipeProductionReadinessGate>();

        Assert.IsType<RuntimeStationFencingTokenValidator>(fencing);
        Assert.NotNull(readiness);
    }
}
