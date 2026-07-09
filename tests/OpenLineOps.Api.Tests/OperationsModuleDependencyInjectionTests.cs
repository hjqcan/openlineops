using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Contract.Services;
using OpenLineOps.Operations.Domain.Repositories;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Operations.Infra.CrossCutting.IoC.DependencyInjection;
using OpenLineOps.Operations.Infra.Data.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class OperationsModuleDependencyInjectionTests
{
    [Fact]
    public void AddOpenLineOpsOperationsModuleUsesEfSqlitePersistenceByDefault()
    {
        using var database = TemporarySqliteDatabase.Create();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Operations:Persistence:ConnectionString"] = database.ConnectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsOperationsModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        Assert.IsType<EfAlarmRepository>(
            scope.ServiceProvider.GetRequiredService<IAlarmRepository>());
    }

    [Fact]
    public void AddOpenLineOpsOperationsModuleCanStillSelectInMemoryPersistence()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Operations:Persistence:Provider"] = OperationsPersistenceProviders.InMemory,
                ["OpenLineOps:Operations:Persistence:DatabaseName"] = $"OpenLineOps.Operations.{Guid.NewGuid():N}"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsOperationsModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        Assert.Equal("Microsoft.EntityFrameworkCore.InMemory", context.Database.ProviderName);
    }

    [Fact]
    public void AddOpenLineOpsOperationsModuleCanSelectPostgreSqlPersistence()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Operations:Persistence:Provider"] = "Postgres",
                ["OpenLineOps:Operations:Persistence:ConnectionString"] =
                    "Host=localhost;Database=openlineops;Username=openlineops;Password=openlineops"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsOperationsModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
    }

    [Fact]
    public void AddOpenLineOpsOperationsModuleRequiresConnectionStringForPostgreSqlPersistence()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Operations:Persistence:Provider"] = OperationsPersistenceProviders.PostgreSql
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsOperationsModule(configuration));

        Assert.Contains("ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddOpenLineOpsOperationsModuleCanRaiseAlarmWithDefaultEfSqlitePersistence()
    {
        using var database = TemporarySqliteDatabase.Create();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Operations:Persistence:ConnectionString"] = database.ConnectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsOperationsModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var appService = scope.ServiceProvider.GetRequiredService<IAlarmAppService>();

        var alarm = await appService.RaiseAsync(new RaiseAlarmRequest(
            $"operations.alarm.di.{Guid.NewGuid():N}",
            "station-di",
            "runtime",
            "session-di",
            AlarmSeverity.Major,
            "Runtime failed",
            "Runtime command failed.",
            DateTimeOffset.UtcNow));

        Assert.Equal("station-di", alarm.StationId);
        Assert.True(File.Exists(database.DatabasePath));
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            DatabasePath = databasePath;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        private string Directory { get; }

        public string DatabasePath { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "operations-ef.sqlite");

            return new TemporarySqliteDatabase(directory, databasePath);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
