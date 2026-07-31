using Microsoft.Data.Sqlite;
using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Persistence;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Infrastructure.Persistence;

namespace OpenLineOps.Maintenance.Tests;

public sealed class SqliteEquipmentAssetRepositoryTests
{
    [Fact]
    public void ConstructorRejectsTransientOrUriDataSources()
    {
        Assert.Throws<ArgumentException>(
            static () => new SqliteEquipmentAssetRepository(
                "Data Source=:memory:"));
        Assert.Throws<ArgumentException>(
            static () => new SqliteEquipmentAssetRepository(
                "Data Source=file:maintenance.db"));
    }

    [Fact]
    public async Task ColdRestartRestoresFactsCountersTasksAndIdempotency()
    {
        using var directory = new TestDirectory();
        var usageCommand = new RecordEquipmentUsageCommand(
            "asset-001",
            5,
            1.25m,
            "usage-command",
            "station-agent",
            MaintenanceTestContext.Now.AddMinutes(5));
        long storedRevision;
        using (var firstRepository =
               new SqliteEquipmentAssetRepository(directory.ConnectionString))
        {
            var service = new EquipmentMaintenanceService(firstRepository);
            await MaintenanceTestContext.RegisterAsync(service);
            var plan = await service.AddPlanAsync(
                new AddMaintenancePlanCommand(
                    "asset-001",
                    "plan-a",
                    "Five-cycle service",
                    CycleInterval: 5,
                    OperatingHoursInterval: null,
                    CalendarInterval: null,
                    BlocksProduction: true,
                    "plan-command",
                    "engineer-001",
                    MaintenanceTestContext.Now));
            Assert.True(plan.IsSuccess, plan.Error.Message);
            var usage = await service.RecordUsageAsync(usageCommand);
            Assert.True(usage.IsSuccess, usage.Error.Message);
            Assert.Single(usage.Value.Tasks);
            storedRevision = usage.Value.Revision;
        }

        using (var restartedRepository =
               new SqliteEquipmentAssetRepository(directory.ConnectionString))
        {
            var restarted = new EquipmentMaintenanceService(restartedRepository);
            var restored = await restarted.GetAsync("asset-001");
            Assert.True(restored.IsSuccess, restored.Error.Message);
            Assert.Equal(storedRevision, restored.Value.Revision);
            Assert.Equal(5, restored.Value.TotalCycles);
            Assert.Equal(1.25m, restored.Value.TotalOperatingHours);
            Assert.Single(restored.Value.Tasks);

            var replay = await restarted.RecordUsageAsync(usageCommand);
            Assert.True(replay.IsSuccess, replay.Error.Message);
            Assert.Equal(storedRevision, replay.Value.Revision);
            Assert.Equal(5, replay.Value.TotalCycles);
        }
    }

    [Fact]
    public async Task StaleSaveIsRejectedAndCurrentStreamIsPreserved()
    {
        using var directory = new TestDirectory();
        using var repository =
            new SqliteEquipmentAssetRepository(directory.ConnectionString);
        var asset = MaintenanceTestContext.RegisterDomainAsset();
        Assert.Equal(
            EquipmentAssetAddResult.Added,
            await repository.TryAddAsync(asset));
        var first = await repository.GetByIdAsync(asset.Id);
        var second = await repository.GetByIdAsync(asset.Id);
        Assert.NotNull(first);
        Assert.NotNull(second);
        first.Asset.RecordUsage(
            1,
            0,
            "usage-one",
            "station-agent",
            MaintenanceTestContext.Now.AddMinutes(1));
        second.Asset.RecordUsage(
            10,
            0,
            "usage-two",
            "station-agent",
            MaintenanceTestContext.Now.AddMinutes(1));

        await repository.SaveAsync(first.Asset, first.Revision);
        await Assert.ThrowsAsync<EquipmentAssetConcurrencyException>(
            () => repository
                .SaveAsync(second.Asset, second.Revision)
                .AsTask());
        var current = await repository.GetByIdAsync(asset.Id);
        Assert.NotNull(current);
        Assert.Equal(1, current.Asset.TotalCycles);
    }

    [Fact]
    public async Task ConcurrentServiceCommandsAreRetriedWithoutLostUsage()
    {
        using var directory = new TestDirectory();
        using var repository =
            new SqliteEquipmentAssetRepository(directory.ConnectionString);
        var service = new EquipmentMaintenanceService(repository);
        await MaintenanceTestContext.RegisterAsync(service);
        var first = service.RecordUsageAsync(
                new RecordEquipmentUsageCommand(
                    "asset-001",
                    1,
                    0.25m,
                    "usage-one",
                    "station-agent",
                    MaintenanceTestContext.Now.AddMinutes(1)))
            .AsTask();
        var second = service.RecordUsageAsync(
                new RecordEquipmentUsageCommand(
                    "asset-001",
                    1,
                    0.25m,
                    "usage-two",
                    "station-agent",
                    MaintenanceTestContext.Now.AddMinutes(1)))
            .AsTask();

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        var current = await service.GetAsync("asset-001");
        Assert.True(current.IsSuccess, current.Error.Message);
        Assert.Equal(2, current.Value.TotalCycles);
        Assert.Equal(0.5m, current.Value.TotalOperatingHours);
    }

    [Fact]
    public async Task FactRowsCannotBeUpdatedOrDeleted()
    {
        using var directory = new TestDirectory();
        using (var repository =
               new SqliteEquipmentAssetRepository(directory.ConnectionString))
        {
            await repository.TryAddAsync(
                MaintenanceTestContext.RegisterDomainAsset());
        }

        await using var connection =
            new SqliteConnection(directory.ConnectionString);
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE maintenance_facts
            SET actor_id = 'tampered'
            WHERE asset_id = 'asset-001';
            """;
        await Assert.ThrowsAsync<SqliteException>(
            () => update.ExecuteNonQueryAsync());
        await using var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM maintenance_facts
            WHERE asset_id = 'asset-001';
            """;
        await Assert.ThrowsAsync<SqliteException>(
            () => delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ListByStationIsStableAndExcludesOtherStations()
    {
        using var directory = new TestDirectory();
        using var repository =
            new SqliteEquipmentAssetRepository(directory.ConnectionString);
        await repository.TryAddAsync(
            MaintenanceTestContext.RegisterDomainAsset(
                assetId: "asset-b",
                stationId: "station-a"));
        await repository.TryAddAsync(
            MaintenanceTestContext.RegisterDomainAsset(
                assetId: "asset-a",
                stationId: "station-a"));
        await repository.TryAddAsync(
            MaintenanceTestContext.RegisterDomainAsset(
                assetId: "asset-c",
                stationId: "station-b"));

        var entries = await repository.ListByStationAsync("station-a");

        Assert.Equal(
            ["asset-a", "asset-b"],
            entries.Select(static entry => entry.Asset.Id.Value));
    }
}
