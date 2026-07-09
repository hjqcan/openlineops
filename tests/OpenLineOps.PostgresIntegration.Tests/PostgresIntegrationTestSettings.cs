using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace OpenLineOps.PostgresIntegration.Tests;

internal static class PostgresIntegrationTestSettings
{
    public const string EnabledEnvironmentVariable = "OPENLINEOPS_RUN_POSTGRES_INTEGRATION";

    public static bool IsEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);

            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string SkipReason =>
        $"Set {EnabledEnvironmentVariable}=1 to run PostgreSQL container integration tests.";
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public PostgresIntegrationFactAttribute()
    {
        if (!PostgresIntegrationTestSettings.IsEnabled)
        {
            Skip = PostgresIntegrationTestSettings.SkipReason;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresContainerGroup : ICollectionFixture<PostgresContainerFixture>
{
    public const string Name = "postgres-container";
}

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("openlineops")
        .WithUsername("openlineops")
        .WithPassword("openlineops")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (!PostgresIntegrationTestSettings.IsEnabled)
        {
            return;
        }

        await _container.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().AsTask().ConfigureAwait(false);
    }
}

internal static class RabbitMqIntegrationTestSettings
{
    public const string EnabledEnvironmentVariable = "OPENLINEOPS_RUN_RABBITMQ_INTEGRATION";

    public static bool IsEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);

            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string SkipReason =>
        $"Set {EnabledEnvironmentVariable}=1 to run RabbitMQ container integration tests.";
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class RabbitMqIntegrationFactAttribute : FactAttribute
{
    public RabbitMqIntegrationFactAttribute()
    {
        if (!RabbitMqIntegrationTestSettings.IsEnabled)
        {
            Skip = RabbitMqIntegrationTestSettings.SkipReason;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqContainerGroup : ICollectionFixture<RabbitMqContainerFixture>
{
    public const string Name = "rabbitmq-container";
}

public sealed class RabbitMqContainerFixture : IAsyncLifetime
{
    private const string ContainerImage = "rabbitmq:3.13-alpine";
    private const string DefaultUserName = "openlineops";
    private const string DefaultPassword = "openlineops";
    private const int BrokerPort = 5672;

    private readonly RabbitMqContainer _container = new RabbitMqBuilder(ContainerImage)
        .WithUsername(DefaultUserName)
        .WithPassword(DefaultPassword)
        .Build();

    public string UserName { get; } = DefaultUserName;

    public string Password { get; } = DefaultPassword;

    public string VirtualHost { get; } = "/";

    public string HostName => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(BrokerPort);

    public async Task InitializeAsync()
    {
        if (!RabbitMqIntegrationTestSettings.IsEnabled)
        {
            return;
        }

        await _container.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().AsTask().ConfigureAwait(false);
    }
}
