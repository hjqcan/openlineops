using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Domain.Identifiers;
using OpenLineOps.Maintenance.Domain.Readiness;

namespace OpenLineOps.Maintenance.Tests;

public sealed class EquipmentAssetDomainTests
{
    [Fact]
    public void CycleAndOperatingHourThresholdsRaiseOneCombinedTask()
    {
        var asset = MaintenanceTestContext.RegisterDomainAsset();
        Assert.True(
            asset.AddMaintenancePlan(
                new MaintenancePlanId("plan-a"),
                "Fixture preventive maintenance",
                cycleInterval: 10,
                operatingHoursInterval: 2.5m,
                calendarInterval: null,
                blocksProduction: true,
                "plan-command",
                "engineer-001",
                MaintenanceTestContext.Now));

        Assert.True(
            asset.RecordUsage(
                cycleDelta: 10,
                operatingHoursDelta: 2.5m,
                "usage-command",
                "station-agent",
                MaintenanceTestContext.Now.AddHours(2.5)));

        var task = Assert.Single(asset.Tasks);
        Assert.Equal(
            MaintenanceDueReason.CycleThreshold
            | MaintenanceDueReason.OperatingHoursThreshold,
            task.DueReason);
        Assert.Equal(10, task.DueAtCycles);
        Assert.Equal(2.5m, task.DueAtOperatingHours);
    }

    [Fact]
    public void CalendarThresholdIsRaisedByExplicitEvaluation()
    {
        var asset = MaintenanceTestContext.RegisterDomainAsset();
        asset.AddMaintenancePlan(
            new MaintenancePlanId("plan-calendar"),
            "Annual verification",
            cycleInterval: null,
            operatingHoursInterval: null,
            calendarInterval: TimeSpan.FromDays(30),
            blocksProduction: true,
            "plan-command",
            "engineer-001",
            MaintenanceTestContext.Now);

        asset.EvaluateDueMaintenance(
            "evaluation-command",
            "scheduler",
            MaintenanceTestContext.Now.AddDays(30));

        var task = Assert.Single(asset.Tasks);
        Assert.Equal(MaintenanceDueReason.CalendarThreshold, task.DueReason);
    }

    [Fact]
    public void CompletingDueTaskResetsBaselineAndUnblocksStart()
    {
        var asset = MaintenanceTestContext.RegisterDomainAsset(
            productionCritical: true,
            requiresCalibration: true);
        asset.RecordHealth(
            EquipmentHealthStatus.Healthy,
            "All checks passed.",
            "health-command",
            "station-agent",
            MaintenanceTestContext.Now);
        asset.RecordCalibration(
            CalibrationStatus.Valid,
            "CAL-2026-001",
            MaintenanceTestContext.Now.AddDays(90),
            "calibration-command",
            "quality-engineer",
            MaintenanceTestContext.Now);
        asset.AddMaintenancePlan(
            new MaintenancePlanId("plan-a"),
            "Cycle maintenance",
            cycleInterval: 2,
            operatingHoursInterval: null,
            calendarInterval: null,
            blocksProduction: true,
            "plan-command",
            "engineer-001",
            MaintenanceTestContext.Now);
        asset.RecordUsage(
            2,
            0,
            "usage-command",
            "station-agent",
            MaintenanceTestContext.Now.AddMinutes(2));

        var blocked = ProductionStartEvaluator.Evaluate(
            [asset],
            MaintenanceTestContext.Now.AddMinutes(3));
        Assert.False(blocked.Allowed);
        Assert.Contains(
            blocked.Blocks,
            static block => block.Reason
                == ProductionStartBlockReason.MaintenanceDue);

        asset.CompleteMaintenanceTask(
            Assert.Single(asset.Tasks).Id,
            "Inspected and lubricated.",
            "complete-command",
            "maintenance-operator",
            MaintenanceTestContext.Now.AddMinutes(4));

        var allowed = ProductionStartEvaluator.Evaluate(
            [asset],
            MaintenanceTestContext.Now.AddMinutes(5));
        Assert.True(allowed.Allowed);
        Assert.Empty(allowed.Blocks);
    }

    [Fact]
    public void ReadinessCombinesCalibrationMaintenanceAndCriticalHealth()
    {
        var asset = MaintenanceTestContext.RegisterDomainAsset(
            productionCritical: true,
            requiresCalibration: true);
        asset.AddMaintenancePlan(
            new MaintenancePlanId("plan-a"),
            "Cycle maintenance",
            cycleInterval: 1,
            operatingHoursInterval: null,
            calendarInterval: null,
            blocksProduction: true,
            "plan-command",
            "engineer-001",
            MaintenanceTestContext.Now);
        asset.RecordUsage(
            1,
            0,
            "usage-command",
            "station-agent",
            MaintenanceTestContext.Now.AddMinutes(1));

        var decision = ProductionStartEvaluator.Evaluate(
            [asset],
            MaintenanceTestContext.Now.AddMinutes(2));

        Assert.False(decision.Allowed);
        Assert.Equal(3, decision.Blocks.Count);
        Assert.Contains(
            decision.Blocks,
            static block => block.Reason
                == ProductionStartBlockReason.CalibrationMissing);
        Assert.Contains(
            decision.Blocks,
            static block => block.Reason
                == ProductionStartBlockReason.MaintenanceDue);
        Assert.Contains(
            decision.Blocks,
            static block => block.Reason
                == ProductionStartBlockReason.CriticalEquipmentHealth);
    }

    [Fact]
    public void ExpiredCalibrationBlocksAtEvaluationTime()
    {
        var asset = MaintenanceTestContext.RegisterDomainAsset(
            requiresCalibration: true);
        asset.RecordCalibration(
            CalibrationStatus.Valid,
            "CAL-2026-001",
            MaintenanceTestContext.Now.AddHours(1),
            "calibration-command",
            "quality-engineer",
            MaintenanceTestContext.Now);

        var decision = ProductionStartEvaluator.Evaluate(
            [asset],
            MaintenanceTestContext.Now.AddHours(1));

        var block = Assert.Single(decision.Blocks);
        Assert.Equal(
            ProductionStartBlockReason.CalibrationExpired,
            block.Reason);
    }

    [Fact]
    public void DuplicateDomainCommandDoesNotAppendOrIncrementUsage()
    {
        var asset = MaintenanceTestContext.RegisterDomainAsset();
        Assert.True(
            asset.RecordUsage(
                1,
                0.25m,
                "usage-command",
                "station-agent",
                MaintenanceTestContext.Now.AddMinutes(1)));
        var factCount = asset.Facts.Count;

        Assert.False(
            asset.RecordUsage(
                500,
                10,
                "usage-command",
                "another-actor",
                MaintenanceTestContext.Now.AddDays(1)));

        Assert.Equal(factCount, asset.Facts.Count);
        Assert.Equal(1, asset.TotalCycles);
        Assert.Equal(0.25m, asset.TotalOperatingHours);
    }
}
