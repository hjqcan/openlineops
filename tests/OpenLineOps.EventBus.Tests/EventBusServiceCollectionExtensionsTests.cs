using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenLineOps.EventBus.DependencyInjection;
using OpenLineOps.Infrastructure.Data.Core.EventBus;

namespace OpenLineOps.EventBus.Tests;

public sealed class EventBusServiceCollectionExtensionsTests
{
    [Fact]
    public void AddOpenLineOpsEventBusThrowsWhenPostgreSqlStorageConnectionStringIsMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:EventBus:UseInMemory"] = "false",
                ["OpenLineOps:EventBus:ConnectionStringName"] = "OpenLineOpsEventBus"
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenLineOpsEventBus(configuration));

        Assert.Contains(
            "ConnectionStrings:OpenLineOpsEventBus",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenLineOpsEventBusThrowsWhenTransactionCoordinatorUsesInMemoryStorage()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:EventBus:UseInMemory"] = "true",
                ["OpenLineOps:EventBus:EnableEfCoreTransactionCoordinator"] = "true"
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenLineOpsEventBus(configuration));

        Assert.Contains(
            "requires PostgreSQL CAP storage",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenLineOpsEventBusRegistersTransactionCoordinatorForPostgreSqlStorage()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OpenLineOpsEventBus"] =
                    "Host=localhost;Database=openlineops;Username=openlineops;Password=openlineops",
                ["OpenLineOps:EventBus:UseInMemory"] = "false",
                ["OpenLineOps:EventBus:ConnectionStringName"] = "OpenLineOpsEventBus",
                ["OpenLineOps:EventBus:EnableEfCoreTransactionCoordinator"] = "true",
                ["OpenLineOps:EventBus:RabbitMq:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOpenLineOpsEventBus(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IIntegrationEventTransactionCoordinator>());
    }
}
