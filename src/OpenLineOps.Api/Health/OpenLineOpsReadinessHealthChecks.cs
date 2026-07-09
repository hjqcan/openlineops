using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenLineOps.EventBus.Configuration;
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
        var builder = services.AddHealthChecks();

        AddOperationsPostgreSqlCheck(builder, configuration);
        AddEventBusPostgreSqlCheck(builder, configuration);
        AddRabbitMqCheck(builder, configuration);

        return services;
    }

    private static void AddOperationsPostgreSqlCheck(
        IHealthChecksBuilder builder,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(OperationsPersistenceSection);
        if (!IsPostgreSqlProvider(section["Provider"]))
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

    private static bool IsPostgreSqlProvider(string? provider)
    {
        return string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
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
}
