using DotNetCore.CAP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.EventBus.DependencyInjection;
using OpenLineOps.Infrastructure.Data.Core.EventBus;

namespace OpenLineOps.EventBus.Tests;

public sealed class EventBusServiceCollectionExtensionsTests
{
    [Fact]
    public void AddOpenLineOpsEventBusRequiresExplicitPublicationMode()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenLineOpsEventBus(new ConfigurationBuilder().Build()));

        Assert.Contains("must be explicitly configured", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("postcommit")]
    [InlineData("Transaction")]
    [InlineData("Enabled")]
    public void AddOpenLineOpsEventBusRejectsNonCanonicalPublicationMode(string mode)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:EventBus:PublicationMode"] = mode
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenLineOpsEventBus(configuration));

        Assert.Contains("'PostCommit' or 'Transactional'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenLineOpsEventBusThrowsWhenPostgreSqlStorageConnectionStringIsMissing()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:EventBus:PublicationMode"] = "PostCommit",
            ["OpenLineOps:EventBus:UseInMemory"] = "false",
            ["OpenLineOps:EventBus:ConnectionStringName"] = "OpenLineOpsEventBus"
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenLineOpsEventBus(configuration));

        Assert.Contains(
            "ConnectionStrings:OpenLineOpsEventBus",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenLineOpsEventBusRejectsTransactionalModeWithInMemoryStorage()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:EventBus:PublicationMode"] = "Transactional",
            ["OpenLineOps:EventBus:UseInMemory"] = "true"
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenLineOpsEventBus(configuration));

        Assert.Contains("requires PostgreSQL CAP storage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostCommitRegistersExplicitPolicyAndOrdinaryPublisherOnly()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsEventBus(BuildConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:EventBus:PublicationMode"] = "PostCommit"
        }));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var policy = scope.ServiceProvider.GetRequiredService<IntegrationEventPublicationPolicy>();
        Assert.Equal(IntegrationEventPublicationMode.PostCommit, policy.Mode);
        Assert.NotNull(scope.ServiceProvider.GetService<IIntegrationEventPublisher>());
        Assert.Null(scope.ServiceProvider.GetService<IIntegrationEventTransactionCoordinator>());

        await StartPublicationValidatorAsync(serviceProvider);
    }

    [Fact]
    public void AddOpenLineOpsEventBusUsesStableUnversionedCapStorageIsolationKey()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsEventBus(BuildConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:EventBus:PublicationMode"] = "PostCommit"
        }));

        using var serviceProvider = services.BuildServiceProvider();
        var capOptions = serviceProvider.GetRequiredService<IOptions<CapOptions>>().Value;

        Assert.Equal("openlineops", capOptions.Version);
        Assert.DoesNotMatch("^v[1-9][0-9]*$", capOptions.Version);
    }

    [Fact]
    public async Task TransactionalRegistersExplicitPolicyCoordinatorAndTransactionalPublisher()
    {
        var services = CreateTransactionalServices();

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var policy = scope.ServiceProvider.GetRequiredService<IntegrationEventPublicationPolicy>();
        Assert.Equal(IntegrationEventPublicationMode.Transactional, policy.Mode);
        Assert.NotNull(scope.ServiceProvider.GetService<ITransactionalIntegrationEventPublisher>());
        Assert.NotNull(scope.ServiceProvider.GetService<IIntegrationEventTransactionCoordinator>());

        await StartPublicationValidatorAsync(serviceProvider);
    }

    [Fact]
    public async Task TransactionalStartupValidationFailsWhenTransactionalPublisherIsMissing()
    {
        var services = CreateTransactionalServices();
        services.RemoveAll<ITransactionalIntegrationEventPublisher>();
        using var serviceProvider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartPublicationValidatorAsync(serviceProvider));

        Assert.Contains(nameof(ITransactionalIntegrationEventPublisher), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransactionalStartupValidationFailsWhenCoordinatorIsMissing()
    {
        var services = CreateTransactionalServices();
        services.RemoveAll<IIntegrationEventTransactionCoordinator>();
        using var serviceProvider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartPublicationValidatorAsync(serviceProvider));

        Assert.Contains(nameof(IIntegrationEventTransactionCoordinator), exception.Message, StringComparison.Ordinal);
    }

    private static ServiceCollection CreateTransactionalServices()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OpenLineOpsEventBus"] =
                "Host=localhost;Database=openlineops;Username=openlineops;Password=openlineops",
            ["OpenLineOps:EventBus:PublicationMode"] = "Transactional",
            ["OpenLineOps:EventBus:UseInMemory"] = "false",
            ["OpenLineOps:EventBus:ConnectionStringName"] = "OpenLineOpsEventBus",
            ["OpenLineOps:EventBus:RabbitMq:Enabled"] = "false"
        });
        var services = new ServiceCollection();
        services.AddOpenLineOpsEventBus(configuration);

        return services;
    }

    private static IConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static async Task StartPublicationValidatorAsync(IServiceProvider serviceProvider)
    {
        var validator = serviceProvider
            .GetServices<IHostedService>()
            .Single(hostedService => hostedService is IntegrationEventPublicationStartupValidator);

        await validator.StartAsync(CancellationToken.None);
    }
}
