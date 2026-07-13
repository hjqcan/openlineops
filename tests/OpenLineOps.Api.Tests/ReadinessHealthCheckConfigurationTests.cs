using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenLineOps.Api.Health;
using OpenLineOps.Operations.Infra.Data.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class ReadinessHealthCheckConfigurationTests
{
    [Fact]
    public void AddOpenLineOpsReadinessHealthChecksRegistersNoExternalChecksForDefaultProfile()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddOpenLineOpsReadinessHealthChecks(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        Assert.Empty(options.Registrations);
    }

    [Fact]
    public void AddOpenLineOpsReadinessHealthChecksRegistersAResolvableHealthCheckService()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddOpenLineOpsReadinessHealthChecks(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        Assert.NotNull(serviceProvider.GetRequiredService<HealthCheckService>());
    }

    [Fact]
    public void AddOpenLineOpsReadinessHealthChecksRegistersPostgreSqlChecksForServerProfile()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OpenLineOpsEventBus"] =
                    "Host=localhost;Database=openlineops;Username=openlineops;Password=openlineops",
                ["OpenLineOps:Operations:Persistence:Provider"] = OperationsPersistenceProviders.PostgreSql,
                ["OpenLineOps:Operations:Persistence:ConnectionString"] =
                    "Host=localhost;Database=openlineops;Username=openlineops;Password=openlineops",
                ["OpenLineOps:EventBus:UseInMemory"] = "false",
                ["OpenLineOps:EventBus:ConnectionStringName"] = "OpenLineOpsEventBus"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddOpenLineOpsReadinessHealthChecks(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var registrations = serviceProvider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value
            .Registrations;

        Assert.Contains(registrations, registration =>
            registration.Name == "openlineops.operations.postgresql"
            && registration.Tags.Contains("operations"));
        Assert.Contains(registrations, registration =>
            registration.Name == "openlineops.eventbus.postgresql"
            && registration.Tags.Contains("eventbus")
            && registration.Tags.Contains("cap"));
        Assert.DoesNotContain(registrations, registration =>
            registration.Name == "openlineops.eventbus.rabbitmq");
    }

    [Fact]
    public void AddOpenLineOpsReadinessHealthChecksRegistersRabbitMqCheckForDeploymentTransport()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OpenLineOpsEventBus"] =
                    "Host=localhost;Database=openlineops;Username=openlineops;Password=openlineops",
                ["OpenLineOps:EventBus:UseInMemory"] = "false",
                ["OpenLineOps:EventBus:ConnectionStringName"] = "OpenLineOpsEventBus",
                ["OpenLineOps:EventBus:RabbitMq:Enabled"] = "true",
                ["OpenLineOps:EventBus:RabbitMq:HostName"] = "localhost",
                ["OpenLineOps:EventBus:RabbitMq:UserName"] = "openlineops",
                ["OpenLineOps:EventBus:RabbitMq:Password"] = "openlineops",
                ["OpenLineOps:EventBus:RabbitMq:VirtualHost"] = "/",
                ["OpenLineOps:EventBus:RabbitMq:Port"] = "5672"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddOpenLineOpsReadinessHealthChecks(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var registrations = serviceProvider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value
            .Registrations;

        Assert.Contains(registrations, registration =>
            registration.Name == "openlineops.eventbus.postgresql");
        Assert.Contains(registrations, registration =>
            registration.Name == "openlineops.eventbus.rabbitmq"
            && registration.Tags.Contains("rabbitmq")
            && registration.Tags.Contains("eventbus")
            && registration.Tags.Contains("cap"));
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("PostgreSQL")]
    [InlineData("postgresql")]
    public void AddOpenLineOpsReadinessHealthChecksRejectsNonCanonicalOperationsProvider(string provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Operations:Persistence:Provider"] = provider
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsReadinessHealthChecks(configuration));

        Assert.Contains("Expected exactly", exception.Message, StringComparison.Ordinal);
    }
}
