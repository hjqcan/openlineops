using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenLineOps.EventBus.Configuration;
using OpenLineOps.Operations.Infra.Data.Persistence;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Transport;
using RabbitMQ.Client;

namespace OpenLineOps.Api.Health;

public static class OpenLineOpsReadinessHealthChecks
{
    private const string OperationsPersistenceSection = "OpenLineOps:Operations:Persistence";

    public static IServiceCollection AddOpenLineOpsReadinessHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions();
        services.AddLogging();
        var builder = services.AddHealthChecks();

        AddOperationsPostgreSqlCheck(builder, configuration);
        AddEventBusPostgreSqlCheck(builder, configuration);
        AddRabbitMqCheck(builder, configuration);
        AddRuntimeCoordinationPostgreSqlCheck(builder, configuration);
        AddStationTransportRabbitMqCheck(builder, configuration);

        return services;
    }

    private static void AddOperationsPostgreSqlCheck(
        IHealthChecksBuilder builder,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(OperationsPersistenceSection);
        var provider = OperationsPersistenceProviders.Parse(
            section["Provider"] ?? OperationsPersistenceProviders.Sqlite);
        if (provider != OperationsPersistenceProvider.PostgreSql)
        {
            return;
        }

        var connectionString = section["ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        builder.AddCheck(
            "openlineops.operations.postgresql",
            new PostgreSqlConnectionHealthCheck(
                connectionString.Trim(),
                "Operations PostgreSQL"),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "postgresql", "operations"]);
    }

    private static void AddEventBusPostgreSqlCheck(
        IHealthChecksBuilder builder,
        IConfiguration configuration)
    {
        var options = new OpenLineOpsEventBusOptions();
        configuration
            .GetSection(OpenLineOpsEventBusOptions.SectionName)
            .Bind(options);

        if (options.UseInMemory)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionStringName))
        {
            return;
        }

        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        builder.AddCheck(
            "openlineops.eventbus.postgresql",
            new PostgreSqlConnectionHealthCheck(
                connectionString.Trim(),
                "EventBus CAP PostgreSQL"),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "postgresql", "eventbus", "cap"]);
    }

    private static void AddRabbitMqCheck(
        IHealthChecksBuilder builder,
        IConfiguration configuration)
    {
        var options = new OpenLineOpsEventBusOptions();
        configuration
            .GetSection(OpenLineOpsEventBusOptions.SectionName)
            .Bind(options);

        if (options.UseInMemory || !options.RabbitMq.Enabled)
        {
            return;
        }

        builder.AddCheck(
            "openlineops.eventbus.rabbitmq",
            new RabbitMqConnectionHealthCheck(options.RabbitMq),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "rabbitmq", "eventbus", "cap"]);
    }

    private static void AddRuntimeCoordinationPostgreSqlCheck(
        IHealthChecksBuilder builder,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(
            ProductionCoordinationPersistenceOptions.SectionName);
        if (!section.Exists())
        {
            return;
        }

        var options = new ProductionCoordinationPersistenceOptions();
        section.Bind(options);
        var provider = ProductionCoordinationPersistenceProviders.Parse(options.Provider);
        if (provider != ProductionCoordinationPersistenceProvider.PostgreSql)
        {
            return;
        }

        builder.AddCheck(
            "openlineops.runtime.coordination.postgresql",
            new PostgreSqlConnectionHealthCheck(
                options.ResolvePostgreSqlConnectionString(),
                "Runtime coordination PostgreSQL"),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "postgresql", "runtime", "coordination"]);
    }

    private static void AddStationTransportRabbitMqCheck(
        IHealthChecksBuilder builder,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(StationCoordinatorTransportOptions.SectionName);
        if (!section.Exists())
        {
            return;
        }

        var options = new StationCoordinatorTransportOptions();
        section.Bind(options);
        var provider = StationCoordinatorTransportProviders.Parse(options.Provider);
        if (provider != StationCoordinatorTransportProvider.RabbitMq)
        {
            return;
        }

        builder.AddCheck(
            "openlineops.runtime.station-transport.rabbitmq",
            new RabbitMqUriConnectionHealthCheck(
                options.ResolveBrokerUri(),
                "Runtime Station transport RabbitMQ"),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "rabbitmq", "runtime", "station-transport"]);
    }

    private sealed class PostgreSqlConnectionHealthCheck(
        string connectionString,
        string dependencyName)
        : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new NpgsqlCommand("SELECT 1;", connection);
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                return HealthCheckResult.Healthy($"{dependencyName} is reachable.");
            }
            catch (Exception exception)
            {
                return HealthCheckResult.Unhealthy(
                    $"{dependencyName} is not reachable.",
                    exception);
            }
        }
    }

    private sealed class RabbitMqConnectionHealthCheck(
        RabbitMqEventBusOptions options)
        : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = options.HostName,
                    UserName = options.UserName,
                    Password = options.Password,
                    VirtualHost = options.VirtualHost,
                    Port = options.Port,
                    AutomaticRecoveryEnabled = false,
                    TopologyRecoveryEnabled = false,
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                    RequestedHeartbeat = TimeSpan.FromSeconds(10)
                };

                await using var connection = await factory
                    .CreateConnectionAsync(
                        "OpenLineOps readiness",
                        cancellationToken)
                    .ConfigureAwait(false);

                return HealthCheckResult.Healthy("EventBus RabbitMQ is reachable.");
            }
            catch (Exception exception)
            {
                return HealthCheckResult.Unhealthy(
                    "EventBus RabbitMQ is not reachable.",
                    exception);
            }
        }
    }

    private sealed class RabbitMqUriConnectionHealthCheck(
        Uri brokerUri,
        string dependencyName)
        : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    Uri = brokerUri,
                    AutomaticRecoveryEnabled = false,
                    TopologyRecoveryEnabled = false,
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                    RequestedHeartbeat = TimeSpan.FromSeconds(10)
                };
                await using var connection = await factory
                    .CreateConnectionAsync(
                        "OpenLineOps runtime readiness",
                        cancellationToken)
                    .ConfigureAwait(false);
                return HealthCheckResult.Healthy($"{dependencyName} is reachable.");
            }
            catch (Exception exception)
            {
                return HealthCheckResult.Unhealthy(
                    $"{dependencyName} is not reachable.",
                    exception);
            }
        }
    }
}
