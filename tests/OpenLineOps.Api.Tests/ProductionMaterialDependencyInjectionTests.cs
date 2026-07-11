using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Infrastructure.Execution;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionMaterialDependencyInjectionTests
{
    [Fact]
    public void InMemoryCoordinationRegistersOneMaterialRepositoryAndScopedService()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(CreateLocalConfiguration(
            ProductionCoordinationPersistenceProviders.InMemory));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var repository = serviceProvider.GetRequiredService<IProductionMaterialRepository>();

        Assert.IsType<InMemoryProductionMaterialRepository>(repository);
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IProductionMaterialRepository));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProductionMaterialService>());
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(
                services,
                descriptor => descriptor.ServiceType == typeof(ProductionMaterialService)).Lifetime);
    }

    [Fact]
    public void SqliteCoordinationUsesItsCoordinationDatabaseForMaterialPersistence()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "coordination.sqlite");
        try
        {
            var services = new ServiceCollection();
            services.AddOpenLineOpsRuntimeModule(CreateLocalConfiguration(
                ProductionCoordinationPersistenceProviders.Sqlite,
                databasePath));

            using var serviceProvider = services.BuildServiceProvider();
            var repository = serviceProvider.GetRequiredService<IProductionMaterialRepository>();

            Assert.IsType<SqliteProductionMaterialRepository>(repository);
            Assert.Single(
                services,
                descriptor => descriptor.ServiceType == typeof(IProductionMaterialRepository));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PostgreSqlCoordinationRegistersAndDisposesItsMaterialDataSource()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Persistence:Provider"] =
                    RuntimeSessionPersistenceProviders.InMemory,
                ["OpenLineOps:Runtime:Coordination:Provider"] =
                    ProductionCoordinationPersistenceProviders.PostgreSql,
                ["OpenLineOps:Runtime:Coordination:ConnectionString"] =
                    "Host=localhost;Database=openlineops;Username=openlineops;Password=not-used",
                [$"{StationCoordinatorTransportOptions.SectionName}:Provider"] =
                    StationCoordinatorTransportProviders.RabbitMq,
                [$"{StationCoordinatorTransportOptions.SectionName}:BrokerUri"] =
                    "amqps://localhost/openlineops",
                [$"{StationExecutionOptions.SectionName}:Provider"] = StationExecutionProviders.Agent
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(configuration);
        var serviceProvider = services.BuildServiceProvider();
        var repository = Assert.IsType<PostgreSqlProductionMaterialRepository>(
            serviceProvider.GetRequiredService<IProductionMaterialRepository>());

        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IProductionMaterialRepository));
        serviceProvider.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await repository.ListProductionUnitsAsync());
    }

    private static IConfiguration CreateLocalConfiguration(string provider, string? databasePath = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["OpenLineOps:Runtime:Persistence:Provider"] =
                RuntimeSessionPersistenceProviders.InMemory,
            ["OpenLineOps:Runtime:Coordination:Provider"] = provider,
            ["OpenLineOps:Runtime:AgentTransport:Provider"] =
                StationCoordinatorTransportProviders.Disabled,
            ["OpenLineOps:Runtime:StationExecution:Provider"] =
                StationExecutionProviders.InProcess
        };
        if (databasePath is not null)
        {
            values["OpenLineOps:Runtime:Coordination:SqliteDatabasePath"] = databasePath;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
