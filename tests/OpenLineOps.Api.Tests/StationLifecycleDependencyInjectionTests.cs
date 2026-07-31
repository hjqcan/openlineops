using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class StationLifecycleDependencyInjectionTests
{
    [Fact]
    public void RuntimeInMemoryProviderRegistersMatchingLifecycleStore()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(Configuration(
            RuntimeSessionPersistenceProviders.InMemory));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<InMemoryStationLifecycleRepository>(
            provider.GetRequiredService<IStationLifecycleRepository>());
        Assert.Same(
            provider.GetRequiredService<IStationLifecycleRepository>(),
            provider.GetRequiredService<InMemoryStationLifecycleRepository>());
        Assert.IsType<InMemoryStationControllerHandshakeRepository>(
            provider.GetRequiredService<IStationControllerHandshakeRepository>());
        Assert.Same(
            provider.GetRequiredService<IStationControllerHandshakeRepository>(),
            provider.GetRequiredService<
                InMemoryStationControllerHandshakeRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<StationLifecycleService>());
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<
                StationControllerHandshakeService>());
    }

    [Fact]
    public void RuntimeSqliteProviderRegistersDurableLifecycleStore()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-station-lifecycle-di-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(Configuration(
            RuntimeSessionPersistenceProviders.Sqlite,
            databasePath));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<SqliteStationLifecycleRepository>(
            provider.GetRequiredService<IStationLifecycleRepository>());
        Assert.Same(
            provider.GetRequiredService<IStationLifecycleRepository>(),
            provider.GetRequiredService<SqliteStationLifecycleRepository>());
        Assert.IsType<SqliteStationControllerHandshakeRepository>(
            provider.GetRequiredService<IStationControllerHandshakeRepository>());
        Assert.Same(
            provider.GetRequiredService<IStationControllerHandshakeRepository>(),
            provider.GetRequiredService<
                SqliteStationControllerHandshakeRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<StationLifecycleService>());
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<
                StationControllerHandshakeService>());
    }

    private static IConfiguration Configuration(string provider, string? databasePath = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Persistence:Provider"] = provider,
                ["OpenLineOps:Runtime:Persistence:ConnectionString"] =
                    databasePath is null ? null : $"Data Source={databasePath};Pooling=False",
                ["OpenLineOps:Runtime:Coordination:Provider"] =
                    ProductionCoordinationPersistenceProviders.InMemory,
                ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess"
            })
            .Build();
    }
}
