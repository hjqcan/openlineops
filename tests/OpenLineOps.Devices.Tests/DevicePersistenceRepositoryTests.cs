using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;
using OpenLineOps.Devices.Infrastructure.Persistence;
using OpenLineOps.Devices.Infrastructure.Persistence.Ef;

namespace OpenLineOps.Devices.Tests;

public sealed class DevicePersistenceRepositoryTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

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
    public async Task EfSqliteDeviceRepositoriesPersistAggregatesWithoutUnusedDomainEvents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = CreateEfOptions(connection);
        var definition = CreateScannerDefinition();
        var instance = CreateScannerInstance("scanner-01", "station-eol");

        Assert.True(instance.RequestConnection().Succeeded);
        Assert.True(instance.ConfirmConnected(BaseTimeUtc.AddSeconds(2)).Succeeded);

        await using (var context = new DevicesDbContext(options))
        {
            await context.Database.MigrateAsync();
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

        await using (var context = new DevicesDbContext(options))
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

    }

    [Fact]
    public async Task EfDeviceStatusRequiresExactCanonicalToken()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateEfOptions(connection);
        var instance = CreateScannerInstance("scanner-canonical", "station-eol");
        Assert.True(instance.RequestConnection().Succeeded);
        Assert.True(instance.ConfirmConnected(BaseTimeUtc).Succeeded);

        await using (var context = new DevicesDbContext(options))
        {
            await context.Database.MigrateAsync();
            await new EfDeviceInstanceRepository(context).SaveAsync(instance);
        }

        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT Status FROM device_instances_ef;";
            Assert.Equal("Connected", await select.ExecuteScalarAsync());
        }

        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE device_instances_ef SET Status = 'connected';";
            await update.ExecuteNonQueryAsync();
        }

        await using var readerContext = new DevicesDbContext(options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            readerContext.DeviceInstances.AsNoTracking().SingleAsync());
        Assert.Contains("case-sensitive", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("connected", exception.ToString(), StringComparison.Ordinal);
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

}
