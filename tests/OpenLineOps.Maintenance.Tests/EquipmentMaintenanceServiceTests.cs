using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Infrastructure.Persistence;

namespace OpenLineOps.Maintenance.Tests;

public sealed class EquipmentMaintenanceServiceTests
{
    [Fact]
    public async Task ExactDuplicateIsIdempotentButChangedReplayIsRejected()
    {
        var service = new EquipmentMaintenanceService(
            new InMemoryEquipmentAssetRepository());
        await MaintenanceTestContext.RegisterAsync(service);
        var command = new RecordEquipmentUsageCommand(
            "asset-001",
            1,
            0.5m,
            "usage-command",
            "station-agent",
            MaintenanceTestContext.Now.AddMinutes(1));

        var first = await service.RecordUsageAsync(command);
        var replay = await service.RecordUsageAsync(command);
        var conflict = await service.RecordUsageAsync(
            command with { CycleDelta = 2 });

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(first.Value.Revision, replay.Value.Revision);
        Assert.Equal(1, replay.Value.TotalCycles);
        Assert.True(conflict.IsFailure);
        Assert.Equal(
            "Conflict.Maintenance.CommandConflict",
            conflict.Error.Code);
    }

    [Fact]
    public async Task StationGateReturnsAssetRevisionsAndCombinedBlocks()
    {
        var service = new EquipmentMaintenanceService(
            new InMemoryEquipmentAssetRepository());
        await MaintenanceTestContext.RegisterAsync(
            service,
            productionCritical: true,
            requiresCalibration: true);

        var decision = await service.EvaluateProductionStartAsync(
            "station-a",
            MaintenanceTestContext.Now);

        Assert.True(decision.IsSuccess, decision.Error.Message);
        Assert.False(decision.Value.Allowed);
        Assert.Equal(2, decision.Value.Blocks.Count);
        var revision = Assert.Single(decision.Value.AssetRevisions);
        Assert.Equal("asset-001", revision.AssetId);
        Assert.Equal(1, revision.Revision);
    }

    [Fact]
    public async Task MaintenanceCompletionThroughServiceUnblocksGate()
    {
        var service = new EquipmentMaintenanceService(
            new InMemoryEquipmentAssetRepository());
        await MaintenanceTestContext.RegisterAsync(service);
        var plan = await service.AddPlanAsync(
            new AddMaintenancePlanCommand(
                "asset-001",
                "plan-a",
                "Daily service",
                CycleInterval: 1,
                OperatingHoursInterval: null,
                CalendarInterval: null,
                BlocksProduction: true,
                "plan-command",
                "engineer-001",
                MaintenanceTestContext.Now));
        Assert.True(plan.IsSuccess, plan.Error.Message);
        var usage = await service.RecordUsageAsync(
            new RecordEquipmentUsageCommand(
                "asset-001",
                1,
                0,
                "usage-command",
                "station-agent",
                MaintenanceTestContext.Now.AddMinutes(1)));
        Assert.True(usage.IsSuccess, usage.Error.Message);
        var task = Assert.Single(usage.Value.Tasks);

        var blocked = await service.EvaluateProductionStartAsync(
            "station-a",
            MaintenanceTestContext.Now.AddMinutes(2));
        Assert.False(blocked.Value.Allowed);
        var completed = await service.CompleteTaskAsync(
            new CompleteMaintenanceTaskCommand(
                "asset-001",
                task.TaskId,
                "Service completed.",
                "complete-command",
                "maintenance-operator",
                MaintenanceTestContext.Now.AddMinutes(3)));
        Assert.True(completed.IsSuccess, completed.Error.Message);

        var allowed = await service.EvaluateProductionStartAsync(
            "station-a",
            MaintenanceTestContext.Now.AddMinutes(4));
        Assert.True(allowed.Value.Allowed);
    }

    [Fact]
    public async Task InMemoryRepositoryDetectsStaleRevision()
    {
        var repository = new InMemoryEquipmentAssetRepository();
        var asset = MaintenanceTestContext.RegisterDomainAsset();
        Assert.Equal(
            OpenLineOps.Maintenance.Application.Persistence
                .EquipmentAssetAddResult.Added,
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
            1,
            0,
            "usage-two",
            "station-agent",
            MaintenanceTestContext.Now.AddMinutes(1));

        await repository.SaveAsync(first.Asset, first.Revision);
        await Assert.ThrowsAsync<
            OpenLineOps.Maintenance.Application.Persistence
                .EquipmentAssetConcurrencyException>(
            () => repository
                .SaveAsync(second.Asset, second.Revision)
                .AsTask());
    }
}
