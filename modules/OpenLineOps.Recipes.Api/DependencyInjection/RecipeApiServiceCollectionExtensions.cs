using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Recipes.Api.Controllers;
using OpenLineOps.Recipes.Application.Fencing;
using OpenLineOps.Recipes.Application.Persistence;
using OpenLineOps.Recipes.Application.Readiness;
using OpenLineOps.Recipes.Application.Recipes;
using OpenLineOps.Recipes.Application.Services;
using OpenLineOps.Recipes.Infrastructure.Engineering;
using OpenLineOps.Recipes.Infrastructure.Persistence;
using OpenLineOps.Recipes.Infrastructure.Time;

namespace OpenLineOps.Recipes.Api.DependencyInjection;

public static class RecipeApiServiceCollectionExtensions
{
    public static IMvcBuilder AddOpenLineOpsRecipesApi(this IMvcBuilder mvcBuilder)
    {
        ArgumentNullException.ThrowIfNull(mvcBuilder);
        return mvcBuilder
            .AddApplicationPart(typeof(RecipeEngineeringController).Assembly)
            .AddJsonOptions(static options =>
            {
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
                options.JsonSerializerOptions.RespectNullableAnnotations = true;
                options.JsonSerializerOptions.UnmappedMemberHandling =
                    JsonUnmappedMemberHandling.Disallow;
            });
    }

    public static IServiceCollection AddOpenLineOpsRecipesModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var effectiveConfiguration =
            configuration ?? new ConfigurationBuilder().Build();
        services.TryAddSingleton<IConfiguration>(effectiveConfiguration);
        services.AddSingleton<
            IValidateOptions<RecipePersistenceOptions>,
            RecipePersistenceOptionsValidator>();
        services.AddOptions<RecipePersistenceOptions>()
            .Bind(effectiveConfiguration.GetSection(RecipePersistenceOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IClock, RecipesSystemClock>();
        services.TryAddScoped<
            IReleasedRecipeRevisionSource,
            EngineeringReleasedRecipeRevisionSource>();
        services.TryAddScoped<
            IStationFencingTokenValidator,
            FailClosedStationFencingTokenValidator>();
        services.AddSingleton<IRecipeOperationsStore>(serviceProvider =>
            new SqliteRecipeOperationsStore(
                serviceProvider
                    .GetRequiredService<IOptions<RecipePersistenceOptions>>()
                    .Value
                    .ResolveConnectionString()));
        services.AddScoped<RecipeOperationsService>();
        services.AddScoped<IRecipeOperationsService>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<RecipeOperationsService>());
        services.AddScoped<IRecipeProductionReadinessGate>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<RecipeOperationsService>());
        services.AddScoped<IStationRecipeAuthorityResolver>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<RecipeOperationsService>());
        return services;
    }
}
