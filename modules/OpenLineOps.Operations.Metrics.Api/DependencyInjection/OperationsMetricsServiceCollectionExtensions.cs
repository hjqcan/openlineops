using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenLineOps.Operations.Metrics.Api.Controllers;
using OpenLineOps.Operations.Metrics.Application.Contracts;
using OpenLineOps.Operations.Metrics.Application.Persistence;
using OpenLineOps.Operations.Metrics.Application.Services;
using OpenLineOps.Operations.Metrics.Infrastructure.Persistence;

namespace OpenLineOps.Operations.Metrics.Api.DependencyInjection;

public static class OperationsMetricsServiceCollectionExtensions
{
    public static IMvcBuilder AddOpenLineOpsOperationsMetricsApi(
        this IMvcBuilder mvcBuilder)
    {
        ArgumentNullException.ThrowIfNull(mvcBuilder);
        return mvcBuilder
            .AddApplicationPart(
                typeof(OperationsMetricsEngineeringController).Assembly)
            .AddJsonOptions(static options =>
            {
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
                options.JsonSerializerOptions.RespectNullableAnnotations = true;
                options.JsonSerializerOptions.UnmappedMemberHandling =
                    JsonUnmappedMemberHandling.Disallow;
            });
    }

    public static IServiceCollection AddOpenLineOpsOperationsMetricsModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var effectiveConfiguration =
            configuration ?? new ConfigurationBuilder().Build();

        services.TryAddSingleton<IConfiguration>(effectiveConfiguration);
        services.AddSingleton<
            IValidateOptions<OperationsMetricsPersistenceOptions>,
            OperationsMetricsPersistenceOptionsValidator>();
        services
            .AddOptions<OperationsMetricsPersistenceOptions>()
            .Bind(effectiveConfiguration.GetSection(
                OperationsMetricsPersistenceOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SqliteOperationsMetricsStore>(
            serviceProvider => new SqliteOperationsMetricsStore(
                serviceProvider
                    .GetRequiredService<
                        IOptions<OperationsMetricsPersistenceOptions>>()
                    .Value
                    .ResolveConnectionString()));
        services.AddSingleton<IOperationsMetricsStore>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<
                    SqliteOperationsMetricsStore>());
        services.AddScoped<IOperationsMetricsService, OperationsMetricsService>();
        return services;
    }
}
