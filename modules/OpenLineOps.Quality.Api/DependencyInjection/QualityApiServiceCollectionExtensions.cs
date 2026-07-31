using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Quality.Api.Controllers;
using OpenLineOps.Quality.Application.Persistence;
using OpenLineOps.Quality.Application.Services;
using OpenLineOps.Quality.Infrastructure.Persistence;
using OpenLineOps.Quality.Infrastructure.Time;

namespace OpenLineOps.Quality.Api.DependencyInjection;

public static class QualityApiServiceCollectionExtensions
{
    public static IMvcBuilder AddOpenLineOpsQualityApi(this IMvcBuilder mvcBuilder)
    {
        ArgumentNullException.ThrowIfNull(mvcBuilder);
        return mvcBuilder.AddApplicationPart(typeof(QualityEngineeringController).Assembly);
    }

    public static IServiceCollection AddOpenLineOpsQualityModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IConfiguration>(
            _ => configuration ?? new ConfigurationBuilder().Build());
        services.AddSingleton<IValidateOptions<QualityPersistenceOptions>, QualityPersistenceOptionsValidator>();
        services
            .AddOptions<QualityPersistenceOptions>()
            .Bind((configuration ?? new ConfigurationBuilder().Build())
                .GetSection(QualityPersistenceOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IClock, QualitySystemClock>();
        services.AddSingleton<IQualityRepository>(serviceProvider =>
            new SqliteQualityRepository(
                serviceProvider
                    .GetRequiredService<IOptions<QualityPersistenceOptions>>()
                    .Value
                    .ResolveSqliteConnectionString()));
        services.AddSingleton<IQualityService, QualityService>();

        return services;
    }
}
