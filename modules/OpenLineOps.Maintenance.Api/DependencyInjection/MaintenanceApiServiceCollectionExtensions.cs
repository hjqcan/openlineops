using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Maintenance.Api.Controllers;
using OpenLineOps.Maintenance.Application.Persistence;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Infrastructure.Persistence;
using OpenLineOps.Maintenance.Infrastructure.Time;

namespace OpenLineOps.Maintenance.Api.DependencyInjection;

public static class MaintenanceApiServiceCollectionExtensions
{
    public static IMvcBuilder AddOpenLineOpsMaintenanceApi(
        this IMvcBuilder mvcBuilder)
    {
        ArgumentNullException.ThrowIfNull(mvcBuilder);
        return mvcBuilder
            .AddApplicationPart(typeof(MaintenanceEngineeringController).Assembly)
            .AddJsonOptions(
                static options =>
                {
                    options.JsonSerializerOptions.PropertyNameCaseInsensitive =
                        false;
                    options.JsonSerializerOptions.RespectNullableAnnotations =
                        true;
                    options.JsonSerializerOptions.UnmappedMemberHandling =
                        JsonUnmappedMemberHandling.Disallow;
                });
    }

    public static IServiceCollection AddOpenLineOpsMaintenanceModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var effectiveConfiguration =
            configuration ?? new ConfigurationBuilder().Build();

        services.TryAddSingleton<IConfiguration>(effectiveConfiguration);
        services.AddSingleton<
            IValidateOptions<MaintenancePersistenceOptions>,
            MaintenancePersistenceOptionsValidator>();
        services
            .AddOptions<MaintenancePersistenceOptions>()
            .Bind(
                effectiveConfiguration.GetSection(
                    MaintenancePersistenceOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IClock, MaintenanceSystemClock>();
        services.AddSingleton<IEquipmentAssetRepository>(
            static serviceProvider =>
                new SqliteEquipmentAssetRepository(
                    serviceProvider
                        .GetRequiredService<
                            IOptions<MaintenancePersistenceOptions>>()
                        .Value
                        .ResolveConnectionString()));
        services.AddScoped<
            IEquipmentMaintenanceService,
            EquipmentMaintenanceService>();
        return services;
    }

    public static IServiceCollection AddOpenLineOpsMaintenanceInMemoryForTests(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IEquipmentAssetRepository>();
        services.AddSingleton<
            IEquipmentAssetRepository,
            InMemoryEquipmentAssetRepository>();
        services.TryAddSingleton<IClock, MaintenanceSystemClock>();
        services.TryAddScoped<
            IEquipmentMaintenanceService,
            EquipmentMaintenanceService>();
        return services;
    }
}
