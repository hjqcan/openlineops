using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Events;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;
using OpenLineOps.Devices.Infrastructure.Persistence;
using OpenLineOps.Devices.Infrastructure.Persistence.Ef;
using OpenLineOps.Domain.Abstractions.Events;

namespace OpenLineOps.Devices.Tests;

public sealed class DevicePersistenceRepositoryTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SqliteDeviceDefinitionRepositoryPersistsDefinitionGraphForNewRepositoryInstance()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteDeviceDefinitionRepository(database.ConnectionString);
        var definition = CreateScannerDefinition();

        await repository.SaveAsync(definition);

        using var restartedRepository = new SqliteDeviceDefinitionRepository(database.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(definition.Id);
        var allDefinitions = await restartedRepository.ListAsync();

        Assert.NotNull(restored);
        Assert.Equal(definition.Id, restored.Id);
        Assert.Equal("scanner-plugin", restored.PluginId);
        Assert.Equal(BaseTimeUtc, restored.CreatedAtUtc);
        Assert.Empty(restored.DomainEvents);
        Assert.Single(allDefinitions);

        var capability = Assert.Single(restored.Capabilities);
        Assert.Equal("device.scanner", capability.Id.Value);
        Assert.Equal("Scanner", capability.DisplayName);

        var command = Assert.Single(restored.Commands);
        Assert.Equal("device.scanner:scan", command.Id.Value);
        Assert.Equal("Scan", command.CommandName);
        Assert.Equal(TimeSpan.FromSeconds(15), command.Timeout);
        Assert.Equal(2, command.MaxRetries);
        Assert.Equal("{\"type\":\"object\"}", command.InputSchema);
    }

    [Fact]
    public async Task SqliteDeviceInstanceRepositoryPersistsConnectionStateAndListsByStation()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteDeviceInstanceRepository(database.ConnectionString);
        var instance = CreateScannerInstance("scanner-01", "station-eol");
        Assert.True(instance.RequestConnection(BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(instance.ConfirmConnected(BaseTimeUtc.AddSeconds(2)).Succeeded);

        await repository.SaveAsync(instance);

        using var restartedRepository = new SqliteDeviceInstanceRepository(database.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(instance.Id);
        var stationInstances = await restartedRepository.ListByStationAsync("station-eol");
        var missingStationInstances = await restartedRepository.ListByStationAsync("station-other");

        Assert.NotNull(restored);
        Assert.Equal(instance.Id, restored.Id);
        Assert.Equal("scanner-definition", restored.DefinitionId.Value);
        Assert.Equal("station-eol", restored.StationId);
        Assert.Equal(DeviceConnectionStatus.Connected, restored.Status);
        Assert.Equal(BaseTimeUtc.AddSeconds(2), restored.ConnectedAtUtc);
        Assert.Null(restored.LastDisconnectedAtUtc);
        Assert.Empty(restored.DomainEvents);
        Assert.Single(stationInstances);
        Assert.Empty(missingStationInstances);
    }

    [Fact]
    public async Task InMemoryDeviceRepositoriesStoreDefinitionsAndInstances()
    {
        var definitionRepository = new InMemoryDeviceDefinitionRepository();
        var instanceRepository = new InMemoryDeviceInstanceRepository();
        var definition = CreateScannerDefinition();
        var firstInstance = CreateScannerInstance("scanner-01", "station-eol");
        var secondInstance = CreateScannerInstance("scanner-02", "station-pack");

        await definitionRepository.SaveAsync(definition);
        await instanceRepository.SaveAsync(secondInstance);
        await instanceRepository.SaveAsync(firstInstance);

        var restoredDefinition = await definitionRepository.GetByIdAsync(definition.Id);
        var stationInstances = await instanceRepository.ListByStationAsync("station-eol");
        var allInstances = await instanceRepository.ListAsync();

        Assert.Same(definition, restoredDefinition);
        Assert.Equal(firstInstance.Id, Assert.Single(stationInstances).Id);
        Assert.Equal([firstInstance.Id, secondInstance.Id], allInstances.Select(instance => instance.Id).ToArray());
    }

    [Fact]
    public async Task EfSqliteDeviceRepositoriesPersistAggregatesAndDispatchDomainEvents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var dispatcher = new CapturingDomainEventDispatcher();
        var options = CreateEfOptions(connection);
        var definition = CreateScannerDefinition();
        var instance = CreateScannerInstance("scanner-01", "station-eol");

        Assert.True(instance.RequestConnection(BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(instance.ConfirmConnected(BaseTimeUtc.AddSeconds(2)).Succeeded);

        await using (var context = new DevicesDbContext(options, dispatcher))
        {
            var definitionRepository = new EfDeviceDefinitionRepository(context);
            var instanceRepository = new EfDeviceInstanceRepository(context);

            await definitionRepository.SaveAsync(definition);
            await instanceRepository.SaveAsync(instance);

            Assert.Empty(instance.DomainEvents);
        }

        await using (var context = new DevicesDbContext(options))
        {
            var definitionRepository = new EfDeviceDefinitionRepository(context);
            var instanceRepository = new EfDeviceInstanceRepository(context);

            var restoredDefinition = await definitionRepository.GetByIdAsync(definition.Id);
            var restoredInstance = await instanceRepository.GetByIdAsync(instance.Id);
            var stationInstances = await instanceRepository.ListByStationAsync("station-eol");

            Assert.NotNull(restoredDefinition);
            Assert.Equal(definition.Id, restoredDefinition.Id);
            Assert.Equal("scanner-plugin", restoredDefinition.PluginId);
            Assert.Empty(restoredDefinition.DomainEvents);

            var capability = Assert.Single(restoredDefinition.Capabilities);
            Assert.Equal("device.scanner", capability.Id.Value);

            var command = Assert.Single(restoredDefinition.Commands);
            Assert.Equal("device.scanner:scan", command.Id.Value);
            Assert.Equal("device.scanner", command.CapabilityId.Value);
            Assert.Equal(TimeSpan.FromSeconds(15), command.Timeout);

            Assert.NotNull(restoredInstance);
            Assert.Equal(DeviceConnectionStatus.Connected, restoredInstance.Status);
            Assert.Equal("tcp", restoredInstance.Endpoint.Protocol);
            Assert.Equal("192.168.1.10:9000", restoredInstance.Endpoint.Address);
            Assert.Empty(restoredInstance.DomainEvents);
            Assert.Equal(restoredInstance.Id, Assert.Single(stationInstances).Id);
        }

        await using (var context = new DevicesDbContext(options, dispatcher))
        {
            var instanceRepository = new EfDeviceInstanceRepository(context);

            var restoredInstance = await instanceRepository.GetByIdAsync(instance.Id);

            Assert.NotNull(restoredInstance);
            Assert.True(restoredInstance.Disconnect(BaseTimeUtc.AddMinutes(1), "operator requested").Succeeded);

            await instanceRepository.SaveAsync(restoredInstance);
        }

        await using (var context = new DevicesDbContext(options))
        {
            var instanceRepository = new EfDeviceInstanceRepository(context);

            var restoredInstance = await instanceRepository.GetByIdAsync(instance.Id);

            Assert.NotNull(restoredInstance);
            Assert.Equal(DeviceConnectionStatus.Disconnected, restoredInstance.Status);
            Assert.Equal(BaseTimeUtc.AddMinutes(1), restoredInstance.LastDisconnectedAtUtc);
        }

        Assert.Contains(
            dispatcher.Dispatched,
            domainEvent => domainEvent is DeviceConnectionStatusChangedDomainEvent changed
                && changed.DeviceInstanceId == instance.Id
                && changed.NewStatus == DeviceConnectionStatus.Connected);
        Assert.Contains(
            dispatcher.Dispatched,
            domainEvent => domainEvent is DeviceConnectionStatusChangedDomainEvent changed
            && changed.DeviceInstanceId == instance.Id
            && changed.NewStatus == DeviceConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task EfSqliteDeviceRepositoriesImportSqliteSnapshotProviderTables()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var snapshotDefinitionRepository = new SqliteDeviceDefinitionRepository(database.ConnectionString);
        using var snapshotInstanceRepository = new SqliteDeviceInstanceRepository(database.ConnectionString);
        var definition = CreateScannerDefinition();
        var instance = CreateScannerInstance("scanner-01", "station-eol");

        Assert.True(instance.RequestConnection(BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(instance.ConfirmConnected(BaseTimeUtc.AddSeconds(2)).Succeeded);

        await snapshotDefinitionRepository.SaveAsync(definition);
        await snapshotInstanceRepository.SaveAsync(instance);

        var options = new DbContextOptionsBuilder<DevicesDbContext>()
            .UseSqlite(database.ConnectionString)
            .Options;

        await using var context = new DevicesDbContext(options);
        var definitionRepository = new EfDeviceDefinitionRepository(context);
        var instanceRepository = new EfDeviceInstanceRepository(context);

        var definitions = await definitionRepository.ListAsync();
        var instances = await instanceRepository.ListByStationAsync("station-eol");
        var restoredDefinition = Assert.Single(definitions);
        var restoredInstance = Assert.Single(instances);

        Assert.Equal(definition.Id, restoredDefinition.Id);
        Assert.Equal("scanner-plugin", restoredDefinition.PluginId);
        Assert.Equal("device.scanner", Assert.Single(restoredDefinition.Capabilities).Id.Value);
        Assert.Equal(instance.Id, restoredInstance.Id);
        Assert.Equal(DeviceConnectionStatus.Connected, restoredInstance.Status);
        Assert.Equal(BaseTimeUtc.AddSeconds(2), restoredInstance.ConnectedAtUtc);
    }

    [Fact]
    public async Task EfSqliteDeviceRepositoriesBootstrapExistingEnsureCreatedSchemaAsMigrated()
    {
        using var database = TemporarySqliteDatabase.Create();
        var options = new DbContextOptionsBuilder<DevicesDbContext>()
            .UseSqlite(database.ConnectionString)
            .Options;

        await using (var ensureCreatedContext = new DevicesDbContext(options))
        {
            await ensureCreatedContext.Database.EnsureCreatedAsync();
        }

        await using (var context = new DevicesDbContext(options))
        {
            var repository = new EfDeviceDefinitionRepository(context);

            await repository.SaveAsync(CreateScannerDefinition());
        }

        await using (var context = new DevicesDbContext(options))
        {
            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();

            Assert.Empty(pendingMigrations);
        }
    }

    private static DeviceDefinition CreateScannerDefinition()
    {
        var definition = DeviceDefinition.Create(
            new DeviceDefinitionId("scanner-definition"),
            "Scanner Definition",
            "scanner-plugin",
            BaseTimeUtc);
        var capability = DeviceCapability.Create(
            new DeviceCapabilityId("device.scanner"),
            "Scanner");
        var command = DeviceCommandDefinition.Create(
            new DeviceCommandDefinitionId("device.scanner:scan"),
            new DeviceCapabilityId("device.scanner"),
            "Scan",
            inputSchema: "{\"type\":\"object\"}",
            outputSchema: "{\"type\":\"object\"}",
            timeout: TimeSpan.FromSeconds(15),
            maxRetries: 2);

        Assert.True(definition.AddCapability(capability).Succeeded);
        Assert.True(definition.AddCommand(command).Succeeded);

        return definition;
    }

    private static DeviceInstance CreateScannerInstance(string id, string stationId)
    {
        return DeviceInstance.Register(
            new DeviceInstanceId(id),
            new DeviceDefinitionId("scanner-definition"),
            stationId,
            id,
            new DeviceEndpoint("tcp", $"192.168.1.{(id.EndsWith("01", StringComparison.Ordinal) ? "10" : "11")}:9000"),
            BaseTimeUtc);
    }

    private static DbContextOptions<DevicesDbContext> CreateEfOptions(SqliteConnection connection)
    {
        return new DbContextOptionsBuilder<DevicesDbContext>()
            .UseSqlite(connection)
            .Options;
    }

    private sealed class CapturingDomainEventDispatcher : IDomainEventDispatcher
    {
        public List<IDomainEvent> Dispatched { get; } = [];

        public Task DispatchAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken = default)
        {
            Dispatched.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        private string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "devices.sqlite");

            System.IO.Directory.CreateDirectory(directory);

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
