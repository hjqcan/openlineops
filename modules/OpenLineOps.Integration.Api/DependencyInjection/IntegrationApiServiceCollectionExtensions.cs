using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenLineOps.Integration.Api.Controllers;
using OpenLineOps.Integration.Api.Security;
using OpenLineOps.Integration.Api.Transport;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Application.Queries;
using OpenLineOps.Integration.Application.WorkOrders;
using OpenLineOps.Integration.Infrastructure.Persistence;

namespace OpenLineOps.Integration.Api.DependencyInjection;

public static class IntegrationApiServiceCollectionExtensions
{
    public static IMvcBuilder AddOpenLineOpsIntegrationApi(this IMvcBuilder mvcBuilder)
    {
        ArgumentNullException.ThrowIfNull(mvcBuilder);
        return mvcBuilder
            .AddApplicationPart(typeof(IntegrationEngineeringController).Assembly)
            .AddJsonOptions(static options =>
            {
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
                options.JsonSerializerOptions.RespectNullableAnnotations = true;
                options.JsonSerializerOptions.UnmappedMemberHandling =
                    JsonUnmappedMemberHandling.Disallow;
            });
    }

    public static IServiceCollection AddOpenLineOpsIntegrationModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var effectiveConfiguration =
            configuration ?? new ConfigurationBuilder().Build();

        services.TryAddSingleton<IConfiguration>(effectiveConfiguration);
        services.AddSingleton<
            IValidateOptions<IntegrationPersistenceOptions>,
            IntegrationPersistenceOptionsValidator>();
        services
            .AddOptions<IntegrationPersistenceOptions>()
            .Bind(effectiveConfiguration.GetSection(
                IntegrationPersistenceOptions.SectionName))
            .ValidateOnStart();
        services
            .AddOptions<IntegrationOutboxWorkerOptions>()
            .Bind(effectiveConfiguration.GetSection(
                IntegrationOutboxWorkerOptions.SectionName))
            .Validate(
                static options => options.IsValid,
                "Integration outbox worker options are outside their safe bounds.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddSingleton<SqliteIntegrationStore>(serviceProvider =>
            new SqliteIntegrationStore(
                serviceProvider
                    .GetRequiredService<IOptions<IntegrationPersistenceOptions>>()
                    .Value
                    .ResolveConnectionString()));
        services.AddSingleton<IIntegrationInboxStore>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<SqliteIntegrationStore>());
        services.AddSingleton<IIntegrationOutboxStore>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<SqliteIntegrationStore>());
        services.AddSingleton<IIntegrationQueryStore>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<SqliteIntegrationStore>());
        services.AddSingleton<IWorkOrderFactStore>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<SqliteIntegrationStore>());

        services.TryAddSingleton<
            IWorkRequestHandler,
            UnconfiguredWorkRequestHandler>();
        services.TryAddSingleton<
            IIntegrationConnector,
            UnconfiguredIntegrationConnector>();
        services.TryAddScoped<
            IIntegrationReplayAuthorizer,
            ClaimsIntegrationReplayAuthorizer>();

        services.AddScoped<WorkOrderService>();
        services.AddScoped<IdempotentWorkRequestService>();
        services.AddScoped<IntegrationOutboxDispatcher>();
        services.AddScoped<IntegrationOutboxReplayService>();
        services.AddHostedService<IntegrationOutboxDispatcherHostedService>();

        return services;
    }
}
