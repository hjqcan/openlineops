using Npgsql;
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
    public const string ExternalConnectionStringEnvironmentVariable =
        "OPENLINEOPS_POSTGRES_CONNECTION_STRING";

    private readonly PostgreSqlContainer? _container;
    private readonly string? _externalConnectionString;
    private string? _isolatedConnectionString;
    private string? _schemaName;

    public PostgresContainerFixture()
    {
        _externalConnectionString = Environment.GetEnvironmentVariable(
            ExternalConnectionStringEnvironmentVariable);
        if (_externalConnectionString is not null
            && (string.IsNullOrWhiteSpace(_externalConnectionString)
                || char.IsWhiteSpace(_externalConnectionString[0])
                || char.IsWhiteSpace(_externalConnectionString[^1])))
        {
            throw new InvalidOperationException(
                $"{ExternalConnectionStringEnvironmentVariable} must be a canonical connection string.");
        }

        if (_externalConnectionString is null)
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("openlineops")
                .WithUsername("openlineops")
                .WithPassword("openlineops")
                .Build();
        }
    }

    public string ConnectionString => _isolatedConnectionString
        ?? throw new InvalidOperationException(
            "PostgreSQL integration fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        if (!PostgresIntegrationTestSettings.IsEnabled)
        {
            return;
        }

        if (_container is not null)
        {
            await _container.StartAsync().ConfigureAwait(false);
        }

        var administrativeConnectionString =
            _externalConnectionString ?? _container!.GetConnectionString();
        _schemaName = $"olo_test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(administrativeConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA {_schemaName};";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        var builder = new NpgsqlConnectionStringBuilder(administrativeConnectionString)
        {
            SearchPath = _schemaName
        };
        _isolatedConnectionString = builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_schemaName is not null && _isolatedConnectionString is not null)
        {
            var builder = new NpgsqlConnectionStringBuilder(_isolatedConnectionString)
            {
                SearchPath = null
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA {_schemaName} CASCADE;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().AsTask().ConfigureAwait(false);
        }
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

[AttributeUsage(AttributeTargets.Method)]
internal sealed class ProductionInfrastructureIntegrationFactAttribute : FactAttribute
{
    public ProductionInfrastructureIntegrationFactAttribute()
    {
        if (!PostgresIntegrationTestSettings.IsEnabled
            || !RabbitMqIntegrationTestSettings.IsEnabled)
        {
            Skip = $"Set {PostgresIntegrationTestSettings.EnabledEnvironmentVariable}=1 and "
                   + $"{RabbitMqIntegrationTestSettings.EnabledEnvironmentVariable}=1 to run "
                   + "the combined production-infrastructure integration gate.";
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqContainerGroup : ICollectionFixture<RabbitMqContainerFixture>
{
    public const string Name = "rabbitmq-container";
}

[CollectionDefinition(Name)]
public sealed class ProductionInfrastructureContainerGroup :
    ICollectionFixture<PostgresContainerFixture>,
    ICollectionFixture<RabbitMqContainerFixture>
{
    public const string Name = "production-infrastructure-containers";
}

public sealed class RabbitMqContainerFixture : IAsyncLifetime
{
    public const string ExternalBrokerUriEnvironmentVariable =
        "OPENLINEOPS_RABBITMQ_URI";

    private const string ContainerImage = "rabbitmq:3.13-alpine";
    private const string DefaultUserName = "openlineops";
    private const string DefaultPassword = "openlineops";
    private const int BrokerPort = 5672;

    private readonly RabbitMqContainer? _container;
    private readonly Uri? _externalBrokerUri;

    public RabbitMqContainerFixture()
    {
        var configured = Environment.GetEnvironmentVariable(
            ExternalBrokerUriEnvironmentVariable);
        if (configured is null)
        {
            _container = new RabbitMqBuilder(ContainerImage)
                .WithUsername(DefaultUserName)
                .WithPassword(DefaultPassword)
                .Build();
            return;
        }

        if (string.IsNullOrWhiteSpace(configured)
            || char.IsWhiteSpace(configured[0])
            || char.IsWhiteSpace(configured[^1])
            || !Uri.TryCreate(configured, UriKind.Absolute, out var brokerUri)
            || !string.Equals(brokerUri.Scheme, "amqp", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(brokerUri.Host)
            || brokerUri.Port <= 0)
        {
            throw new InvalidOperationException(
                $"{ExternalBrokerUriEnvironmentVariable} must be a canonical absolute amqp URI.");
        }

        var credentials = brokerUri.UserInfo.Split(':', 2);
        if (credentials.Length != 2
            || credentials.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"{ExternalBrokerUriEnvironmentVariable} must include username and password.");
        }

        _externalBrokerUri = brokerUri;
    }

    public string UserName => _externalBrokerUri is null
        ? DefaultUserName
        : Uri.UnescapeDataString(_externalBrokerUri.UserInfo.Split(':', 2)[0]);

    public string Password => _externalBrokerUri is null
        ? DefaultPassword
        : Uri.UnescapeDataString(_externalBrokerUri.UserInfo.Split(':', 2)[1]);

    public string VirtualHost
    {
        get
        {
            if (_externalBrokerUri is null)
            {
                return "/";
            }

            var encoded = _externalBrokerUri.AbsolutePath.TrimStart('/');
            return encoded.Length == 0 ? "/" : Uri.UnescapeDataString(encoded);
        }
    }

    public string HostName => _externalBrokerUri?.Host ?? _container!.Hostname;

    public int Port => _externalBrokerUri?.Port
        ?? _container!.GetMappedPublicPort(BrokerPort);

    public async Task InitializeAsync()
    {
        if (!RabbitMqIntegrationTestSettings.IsEnabled)
        {
            return;
        }

        if (_container is not null)
        {
            await _container.StartAsync().ConfigureAwait(false);
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().AsTask().ConfigureAwait(false);
        }
    }
}
