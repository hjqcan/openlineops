using DotNetCore.CAP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.EventBus.Cap;
using OpenLineOps.EventBus.Configuration;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using Savorboard.CAP.InMemoryMessageQueue;

namespace OpenLineOps.EventBus.DependencyInjection;

public static class EventBusServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsEventBus(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = LoadOptions(configuration);
        ValidateOptions(options, configuration);
        services.AddSingleton(options);
        services.TryAddSingleton<IntegrationDtoConverterRegistry>();
        services.TryAddScoped<IIntegrationEventPublisher, CapIntegrationEventPublisher>();
        services.TryAddScoped<ITransactionalIntegrationEventPublisher, CapIntegrationEventPublisher>();
        if (options.EnableEfCoreTransactionCoordinator)
        {
            services.TryAddScoped<IIntegrationEventTransactionCoordinator, CapEfCoreIntegrationEventTransactionCoordinator>();
        }

        services.AddCap(capOptions =>
        {
            if (options.UseInMemory)
            {
                capOptions.UseInMemoryStorage();
                capOptions.UseInMemoryMessageQueue();
            }
            else
            {
                capOptions.UsePostgreSql(postgresOptions =>
                {
                    postgresOptions.ConnectionString = configuration.GetConnectionString(options.ConnectionStringName);
                    postgresOptions.Schema = options.PostgreSqlSchema;
                });

                if (options.RabbitMq.Enabled)
                {
                    capOptions.UseRabbitMQ(rabbitOptions =>
                    {
                        rabbitOptions.HostName = options.RabbitMq.HostName;
                        rabbitOptions.UserName = options.RabbitMq.UserName;
                        rabbitOptions.Password = options.RabbitMq.Password;
                        rabbitOptions.VirtualHost = options.RabbitMq.VirtualHost;
                        rabbitOptions.ExchangeName = options.RabbitMq.ExchangeName;
                        rabbitOptions.Port = options.RabbitMq.Port;
                    });
                }
                else
                {
                    capOptions.UseInMemoryMessageQueue();
                }
            }

            capOptions.FailedRetryCount = options.FailedRetryCount;
            capOptions.FailedRetryInterval = options.FailedRetryIntervalSeconds;
            capOptions.EnablePublishParallelSend = true;
            capOptions.Version = "v1";
            capOptions.SucceedMessageExpiredAfter = options.SucceedMessageExpiredAfterSeconds;
            capOptions.FailedMessageExpiredAfter = options.FailedMessageExpiredAfterSeconds;
            capOptions.ConsumerThreadCount = options.ConsumerThreadCount;

            if (options.UseDashboard)
            {
                capOptions.UseDashboard();
            }
        });

        return services;
    }

    private static OpenLineOpsEventBusOptions LoadOptions(IConfiguration configuration)
    {
        var options = new OpenLineOpsEventBusOptions();
        configuration.GetSection(OpenLineOpsEventBusOptions.SectionName).Bind(options);

        return options;
    }

    private static void ValidateOptions(
        OpenLineOpsEventBusOptions options,
        IConfiguration configuration)
    {
        if (options.UseInMemory)
        {
            if (options.EnableEfCoreTransactionCoordinator)
            {
                throw new InvalidOperationException(
                    "OpenLineOps EventBus EF Core transaction coordination requires PostgreSQL CAP storage. Set OpenLineOps:EventBus:UseInMemory to false or disable OpenLineOps:EventBus:EnableEfCoreTransactionCoordinator.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionStringName))
        {
            throw new InvalidOperationException(
                "OpenLineOps EventBus PostgreSQL storage requires OpenLineOps:EventBus:ConnectionStringName when OpenLineOps:EventBus:UseInMemory is false.");
        }

        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"OpenLineOps EventBus PostgreSQL storage requires ConnectionStrings:{options.ConnectionStringName} when OpenLineOps:EventBus:UseInMemory is false.");
        }
    }
}
