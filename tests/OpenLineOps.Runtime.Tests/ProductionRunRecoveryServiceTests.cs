using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Events;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunRecoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunningHardwareOperationEntersRecoveryRequiredWithoutReplay()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var (run, plan) = CreateRun();
        Assert.True(await repository.TryAddAsync(
            run,
            plan,
            await ProductionRunTestMaterials.RegisterAsync(materials, run)));
        Assert.True(run.Start(Now).Succeeded);
        var operation = Assert.Single(run.Operations);
        var leases = operation.ResourceRequirements.Select(resource => new ResourceLease(
            resource,
            run.Id,
            operation.OperationRunId,
            1,
            Now,
            Now.AddHours(1))).ToArray();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            Now).Succeeded);
        await repository.SaveAsync(run, 0);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var service = new ProductionRunRecoveryService(
            repository,
            new InMemoryResourceLeaseRepository(),
            publisher,
            new FixedClock(Now.AddMinutes(1)));

        var result = await service.RecoverAsync();
        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id)).Run;

        Assert.Equal(1, result.RecoveryRequiredRunCount);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, restored.ControlState);
        Assert.Equal(ExecutionStatus.Running, restored.ExecutionStatus);
        Assert.Equal(ExecutionStatus.Running, Assert.Single(restored.Operations).ExecutionStatus);
    }

    [Fact]
    public async Task PendingRunRemainsDispatchableAfterRestart()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var (run, plan) = CreateRun();
        Assert.True(await repository.TryAddAsync(
            run,
            plan,
            await ProductionRunTestMaterials.RegisterAsync(materials, run)));
        var service = new ProductionRunRecoveryService(
            repository,
            new InMemoryResourceLeaseRepository(),
            new InMemoryRuntimeDomainEventPublisher(),
            new FixedClock(Now));

        var result = await service.RecoverAsync();

        Assert.Equal(1, result.PendingRunCount);
        Assert.Equal(ExecutionStatus.Pending, (await repository.GetByIdAsync(run.Id))!.Run.ExecutionStatus);
    }

    private static (ProductionRun, ProductionRunExecutionPlan) CreateRun()
    {
        var runId = ProductionRunId.New();
        var operation = new OperationExecutionPlan(
            "operation.main",
            "station.main",
            new StationId("station.main"),
            new ConfigurationSnapshotId("configuration.main"),
            new RecipeSnapshotId("recipe.main"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process.main"),
                new ProcessVersionId("process-version.main"),
                []));
        var run = ProductionRun.Create(
            runId,
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-001"),
            null,
            null,
            "operator.main",
            operation.Definition.OperationId,
            Now,
            [operation.Definition],
            []);
        return (run, new ProductionRunExecutionPlan(runId, [operation]));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
