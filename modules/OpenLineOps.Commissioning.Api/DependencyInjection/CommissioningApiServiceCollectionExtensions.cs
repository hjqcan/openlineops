using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Commissioning.Api.Controllers;
using OpenLineOps.Commissioning.Api.Security;
using OpenLineOps.Commissioning.Application.Persistence;
using OpenLineOps.Commissioning.Application.Security;
using OpenLineOps.Commissioning.Application.Services;
using OpenLineOps.Commissioning.Infrastructure.Persistence;
using OpenLineOps.Commissioning.Infrastructure.Time;

namespace OpenLineOps.Commissioning.Api.DependencyInjection;

public static class CommissioningApiServiceCollectionExtensions
{
    public static IMvcBuilder AddOpenLineOpsCommissioningApi(
        this IMvcBuilder mvcBuilder)
    {
        ArgumentNullException.ThrowIfNull(mvcBuilder);
        return mvcBuilder.AddApplicationPart(
            typeof(CommissioningEngineeringController).Assembly);
    }

    public static IServiceCollection AddOpenLineOpsCommissioningModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var effectiveConfiguration =
            configuration ?? new ConfigurationBuilder().Build();

        services.TryAddSingleton<IConfiguration>(effectiveConfiguration);
        services.AddSingleton<
            IValidateOptions<CommissioningPersistenceOptions>,
            CommissioningPersistenceOptionsValidator>();
        services.AddSingleton<
            IValidateOptions<CommissioningAuthorizationOptions>,
            CommissioningAuthorizationOptionsValidator>();
        services
            .AddOptions<CommissioningPersistenceOptions>()
            .Bind(effectiveConfiguration.GetSection(
                CommissioningPersistenceOptions.SectionName))
            .ValidateOnStart();
        services
            .AddOptions<CommissioningAuthorizationOptions>()
            .Bind(effectiveConfiguration.GetSection(
                CommissioningAuthorizationOptions.SectionName))
            .ValidateOnStart();

        services.AddHttpContextAccessor();
        services.TryAddSingleton<IClock, CommissioningSystemClock>();
        services.AddSingleton<ICommissioningSessionRepository>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<CommissioningPersistenceOptions>>()
                .Value;
            return options.Provider switch
            {
                CommissioningPersistenceOptions.InMemoryProvider =>
                    new InMemoryCommissioningSessionRepository(),
                CommissioningPersistenceOptions.SqliteProvider =>
                    new SqliteCommissioningSessionRepository(
                        options.ResolveSqliteConnectionString()),
                _ => throw new InvalidOperationException(
                    $"Unsupported Commissioning persistence provider '{options.Provider}'.")
            };
        });
        services.AddScoped<ICommissioningAccessPolicy, ClaimsCommissioningAccessPolicy>();
        services.AddScoped<ICommissioningService, CommissioningService>();

        return services;
    }
}
