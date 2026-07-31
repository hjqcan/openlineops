using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Maintenance.Api.RuntimeIntegration;
using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Infrastructure.Persistence;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Maintenance.Tests;

public sealed class MaintenanceRuntimeGateTests
{
    [Fact]
    public async Task ReadyBaselineIsBlockedWithNormalizedBlocksAndRevisionEvidence()
    {
        var service = new EquipmentMaintenanceService(
            new InMemoryEquipmentAssetRepository());
        Assert.True(
            (await service.RegisterAsync(
                Registration(
                    "asset-a",
                    productionCritical: true,
                    requiresCalibration: true))).IsSuccess);
        Assert.True(
            (await service.RegisterAsync(
                Registration(
                    "asset-b",
                    productionCritical: false,
                    requiresCalibration: false))).IsSuccess);
        Assert.True(
            (await service.AddPlanAsync(
                new AddMaintenancePlanCommand(
                    "asset-b",
                    "plan-b",
                    "Cycle service",
                    CycleInterval: 1,
                    OperatingHoursInterval: null,
                    CalendarInterval: null,
                    BlocksProduction: true,
                    "plan-b",
                    "engineer-a",
                    MaintenanceTestContext.Now))).IsSuccess);
        Assert.True(
            (await service.RecordUsageAsync(
                new RecordEquipmentUsageCommand(
                    "asset-b",
                    1,
                    0,
                    "usage-b",
                    "agent-a",
                    MaintenanceTestContext.Now.AddMinutes(1)))).IsSuccess);
        var baseline = new ProductionOperationReadiness(
            ProductionOperationReadinessKind.Ready,
            "Baseline ready.",
            [new ResourceRequirement(ResourceKind.Station, "station-a")],
            "base:revision");
        var gate = new MaintenanceAwareProductionOperationReadiness(
            new StubReadiness(baseline),
            service,
            new FixedClock(MaintenanceTestContext.Now.AddMinutes(2)));
        var (run, operation) = RuntimeSnapshots();

        var result = await gate.EvaluateAsync(run, operation);

        Assert.Equal(ProductionOperationReadinessKind.Waiting, result.Kind);
        Assert.Contains(
            "asset=asset-a,reason=CalibrationMissing,subject=asset-a",
            result.Reason,
            StringComparison.Ordinal);
        Assert.Contains(
            "asset=asset-a,reason=CriticalEquipmentHealth,subject=asset-a",
            result.Reason,
            StringComparison.Ordinal);
        Assert.Contains(
            "asset=asset-b,reason=MaintenanceDue,subject=plan-b-T000001",
            result.Reason,
            StringComparison.Ordinal);
        Assert.Equal(baseline.MaterialResources, result.MaterialResources);
        Assert.Equal(
            "base:revision|maintenance:station-a:asset-a:1"
            + "|maintenance:station-a:asset-b:4",
            result.EvidenceKey);
    }

    [Fact]
    public async Task AllowedMaintenancePreservesReadyResultAndBindsEveryRevision()
    {
        var service = new EquipmentMaintenanceService(
            new InMemoryEquipmentAssetRepository());
        Assert.True(
            (await service.RegisterAsync(
                Registration(
                    "asset-a",
                    productionCritical: false,
                    requiresCalibration: false))).IsSuccess);
        Assert.True(
            (await service.RegisterAsync(
                Registration(
                    "asset-b",
                    productionCritical: false,
                    requiresCalibration: false))).IsSuccess);
        var baseline = new ProductionOperationReadiness(
            ProductionOperationReadinessKind.Ready,
            "Baseline ready.",
            [],
            "base:revision");
        var gate = new MaintenanceAwareProductionOperationReadiness(
            new StubReadiness(baseline),
            service,
            new FixedClock(MaintenanceTestContext.Now));
        var (run, operation) = RuntimeSnapshots();

        var result = await gate.EvaluateAsync(run, operation);

        Assert.Equal(ProductionOperationReadinessKind.Ready, result.Kind);
        Assert.Equal("Baseline ready.", result.Reason);
        Assert.Equal(
            "base:revision|maintenance:station-a:asset-a:1"
            + "|maintenance:station-a:asset-b:1",
            result.EvidenceKey);
    }

    [Fact]
    public async Task NonReadyBaselineShortCircuitsMaintenanceEvaluation()
    {
        var baseline = new ProductionOperationReadiness(
            ProductionOperationReadinessKind.RecoveryRequired,
            "Material recovery required.",
            [],
            "material:evidence");
        var service = new EquipmentMaintenanceService(
            new ThrowingEquipmentAssetRepository());
        var gate = new MaintenanceAwareProductionOperationReadiness(
            new StubReadiness(baseline),
            service,
            new FixedClock(MaintenanceTestContext.Now));
        var (run, operation) = RuntimeSnapshots();

        var result = await gate.EvaluateAsync(run, operation);

        Assert.Same(baseline, result);
    }

    [Fact]
    public void RuntimeGateExtensionReplacesInterfaceWithConcreteInnerAndWrapper()
    {
        var services = new ServiceCollection();
        services.AddScoped<IProductionOperationReadiness>(
            static _ => new StubReadiness(
                ProductionOperationReadiness.Ready));

        services.AddOpenLineOpsMaintenanceRuntimeGate();

        Assert.Single(
            services,
            static descriptor => descriptor.ServiceType
                == typeof(IProductionOperationReadiness));
        Assert.Single(
            services,
            static descriptor => descriptor.ServiceType
                == typeof(ProductionOperationReadinessEvaluator));
        Assert.Single(
            services,
            static descriptor => descriptor.ServiceType
                == typeof(MaintenanceAwareProductionOperationReadiness));
    }

    private static RegisterEquipmentAssetCommand Registration(
        string assetId,
        bool productionCritical,
        bool requiresCalibration) =>
        new(
            assetId,
            "station-a",
            $"Asset {assetId}",
            productionCritical,
            requiresCalibration,
            $"register-{assetId}",
            "engineer-a",
            MaintenanceTestContext.Now);

    private static (ProductionRunSnapshot Run, OperationRunSnapshot Operation)
        RuntimeSnapshots()
    {
        var definition = new OperationRunDefinition(
            "operation-a",
            "station-a",
            new StationId("station-a"),
            new ProcessDefinitionId("process-a"),
            new ProcessVersionId("process-v1"),
            new ConfigurationSnapshotId("configuration-v1"),
            new RecipeSnapshotId("recipe-v1"));
        var operation = new OperationRunSnapshot(
            definition,
            "operation-run-a",
            Attempt: 1,
            ExecutionStatus.Pending,
            ResultJudgement.Unknown,
            RuntimeSessionId: null,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            FailureCode: null,
            FailureReason: null,
            CompletedStepCount: 0,
            CommandCount: 0,
            IncidentCount: 0,
            RecoveryDecisionId: null,
            ExecutionEvidence: null,
            Outputs: new Dictionary<string, ProductionContextValue>(),
            FencingTokens: new Dictionary<ResourceRequirement, long>(),
            SourceOperationRunBindings: new Dictionary<string, string>());
        var run = new ProductionRunSnapshot(
            ProductionRunId.New(),
            "project-a",
            "application-a",
            "snapshot-a",
            "topology-a",
            "line-a",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("model-a", "SerialNumber", "SN-001"),
            LotId: null,
            CarrierId: null,
            "actor-a",
            ExecutionStatus.Pending,
            ResultJudgement.Unknown,
            ProductDisposition.InProcess,
            ProductionRunControlState.Active,
            SafeStopRequestedBy: null,
            SafeStopReason: null,
            SafeStopRequestedAtUtc: null,
            SafeStopAcknowledgedAtUtc: null,
            ScrapRequestedBy: null,
            ScrapReason: null,
            ScrapRequestedAtUtc: null,
            MaintenanceTestContext.Now,
            MaintenanceTestContext.Now,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            FailureCode: null,
            FailureReason: null,
            "operation-a",
            [definition],
            [],
            [operation],
            [],
            new Dictionary<string, int>(),
            []);
        return (run, operation);
    }

    private sealed class StubReadiness(ProductionOperationReadiness result) :
        IProductionOperationReadiness
    {
        public ValueTask<ProductionOperationReadiness> EvaluateAsync(
            ProductionRunSnapshot run,
            OperationRunSnapshot operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
