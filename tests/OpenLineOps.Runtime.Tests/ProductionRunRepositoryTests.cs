using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryRepositoryAtomicallyStoresRunAndFrozenExecutionPlan()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var (run, plan) = CreateRun();

        Assert.True(await repository.TryAddAsync(
            run,
            plan,
            await ProductionRunTestMaterials.RegisterAsync(materials, run)));
        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id));
        var restoredPlan = Assert.IsType<ProductionRunExecutionPlan>(
            await repository.GetByRunIdAsync(run.Id));

        Assert.Equal(0, restored.Revision);
        Assert.Equal("product.board", restored.Run.ProductionUnitIdentity.ModelId);
        Assert.Equal("operation.main", Assert.Single(restoredPlan.Operations).Definition.OperationId);
    }

    [Fact]
    public async Task SqliteRepositoryRoundTripsDualAxesAndTypedOutput()
    {
        await using var database = new TemporaryDatabase();
        using var repository = new SqliteProductionRunRepository(database.ConnectionString);
        using var materials = new SqliteProductionMaterialRepository(database.ConnectionString);
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
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Failed,
            new Dictionary<string, ProductionContextValue>
            {
                ["measuredVoltage"] = new(
                    ProductionContextValueKind.FixedPoint,
                    "3.30")
            },
            1,
            1,
            0,
            Now.AddSeconds(1)).Succeeded);

        Assert.Equal(1, await repository.SaveAsync(run, 0));
        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id));

        Assert.Equal(ExecutionStatus.Completed, restored.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, restored.Run.Judgement);
        Assert.Equal(ProductDisposition.Nonconforming, restored.Run.Disposition);
        Assert.Equal(
            "3.30",
            Assert.Single(restored.Run.Operations).Outputs["measuredVoltage"].CanonicalValue);
        Assert.NotNull(await repository.GetByRunIdAsync(run.Id));
        var timeline = await materials.ListTimelineAsync(new ProductionMaterialTimelineQuery(
            productionUnitId: run.ProductionUnitId,
            productionRunId: run.Id));
        var disposition = Assert.Single(timeline, entry =>
            entry.Kind == ProductionMaterialEvidenceKind.DispositionTransition);
        Assert.Equal(ProductDisposition.InProcess, disposition.PreviousDisposition);
        Assert.Equal(ProductDisposition.Nonconforming, disposition.CurrentDisposition);
        Assert.Equal(run.Id, disposition.ProductionRunId);
    }

    [Fact]
    public async Task SqliteRepositoryRoundTripsImmutableRecoveryDecisionEvidence()
    {
        await using var database = new TemporaryDatabase();
        using var repository = new SqliteProductionRunRepository(database.ConnectionString);
        using var materials = new SqliteProductionMaterialRepository(database.ConnectionString);
        var (run, plan) = CreateRun();
        Assert.True(await repository.TryAddAsync(
            run,
            plan,
            await ProductionRunTestMaterials.RegisterAsync(materials, run)));
        Assert.True(run.Start(Now).Succeeded);
        var operation = Assert.Single(run.Operations);
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            operation.ResourceRequirements.Select(resource => new ResourceLease(
                resource,
                run.Id,
                operation.OperationRunId,
                9,
                Now,
                Now.AddHours(1))).ToArray(),
            Now).Succeeded);
        Assert.True(run.MarkRecoveryRequired("Agent process stopped.", Now.AddSeconds(1)).Succeeded);
        var decision = new ProductionRecoveryDecision(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            ProductionRecoveryDecisionKind.Retry,
            "operator.main",
            "Station inspection permits an explicit retry.",
            "inspection:sqlite-recovery-001",
            Now.AddSeconds(2),
            operationId: operation.OperationId);
        Assert.True(run.RetryRecovery(decision).Succeeded);

        Assert.Equal(1, await repository.SaveAsync(run, 0));
        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id));

        var storedDecision = Assert.Single(restored.Run.RecoveryDecisions);
        Assert.Equal(decision.DecisionId, storedDecision.DecisionId);
        Assert.Equal(decision.EvidenceReference, storedDecision.EvidenceReference);
        Assert.Equal(ProductionRecoveryDecisionKind.Retry, storedDecision.Kind);
        Assert.Equal("operation.main@0002", restored.Run.Operations[^1].OperationRunId);
        Assert.Equal(ExecutionStatus.Pending, restored.Run.Operations[^1].ExecutionStatus);
    }

    [Fact]
    public async Task RepositoryRejectsStaleAggregateRevision()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var (run, plan) = CreateRun();
        Assert.True(await repository.TryAddAsync(
            run,
            plan,
            await ProductionRunTestMaterials.RegisterAsync(materials, run)));
        Assert.True(run.Start(Now).Succeeded);
        Assert.Equal(1, await repository.SaveAsync(run, 0));

        await Assert.ThrowsAsync<ProductionRunConcurrencyException>(async () =>
            await repository.SaveAsync(run, 0));
    }

    [Fact]
    public async Task SqliteRunSaveOwnsRunningSlotUntilOperationResultThenAllowsUnload()
    {
        await using var database = new TemporaryDatabase();
        using var repository = new SqliteProductionRunRepository(database.ConnectionString);
        using var materials = new SqliteProductionMaterialRepository(database.ConnectionString);
        var unitId = ProductionUnitId.New();
        var slotAddress = new SlotAddress("line.main", "station.main", "slot.main");
        var material = MaterialReference.ForProductionUnit(unitId);
        var station = MaterialLocation.AtStation(slotAddress.LineId, slotAddress.StationSystemId);
        var unit = ProductionUnit.Register(
            unitId,
            "product.board",
            "serialNumber",
            "SN-SLOT-001",
            null,
            "operator.main",
            Now.AddSeconds(-10));
        var slot = SlotOccupancy.Register(slotAddress, Now.AddSeconds(-10));
        Assert.True(await materials.TryAddAsync(unit));
        Assert.True(await materials.TryAddAsync(slot));
        var materialService = new ProductionMaterialService(materials, repository);
        Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
            material,
            station,
            "scanner.main",
            Now.AddSeconds(-9)))).Succeeded);
        Assert.True((await materialService.ReserveSlotAsync(new ReserveSlotCommand(
            slotAddress,
            material,
            "coordinator.main",
            Now.AddSeconds(-8)))).Succeeded);
        Assert.True((await materialService.LoadSlotAsync(new LoadSlotCommand(
            slotAddress,
            material,
            "operator.main",
            Now.AddSeconds(-6)))).Succeeded);
        Assert.True((await materialService.StartSlotAsync(new StartSlotCommand(
            slotAddress,
            material,
            "agent.main",
            Now.AddSeconds(-4)))).Succeeded);

        var (run, plan) = CreateRun(unitId, "SN-SLOT-001");
        var unitEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await materials.GetProductionUnitAsync(unitId));
        Assert.True(await repository.TryAddAsync(
            run,
            plan,
            new ProductionRunAdmission(unitEntry.Aggregate.ToSnapshot(), unitEntry.Revision)));
        Assert.True(run.Start(Now).Succeeded);
        var operation = Assert.Single(run.Operations);
        var stationResource = Assert.Single(operation.ResourceRequirements);
        var slotResource = new ResourceRequirement(ResourceKind.Slot, slotAddress.ToString());
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            [
                new ResourceLease(stationResource, run.Id, operation.OperationRunId, 1, Now, Now.AddMinutes(1)),
                new ResourceLease(slotResource, run.Id, operation.OperationRunId, 2, Now, Now.AddMinutes(1))
            ],
            Now).Succeeded);

        Assert.Equal(1, await repository.SaveAsync(run, 0));
        var runningSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await materials.GetSlotAsync(slotAddress));
        Assert.Equal(SlotOccupancyStatus.Running, runningSlot.Aggregate.Status);
        Assert.Equal(2, Assert.Single(run.Operations).FencingTokens[slotResource]);

        var manualCompletion = await materialService.CompleteSlotAsync(new CompleteSlotCommand(
            slotAddress,
            material,
            "operator.main",
            Now.AddSeconds(1)));
        Assert.False(manualCompletion.Succeeded);
        Assert.Equal("Runtime.ProductionMaterialRunOwnsSlotLifecycle", manualCompletion.Code);

        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            null,
            1,
            1,
            0,
            Now.AddSeconds(2)).Succeeded);
        Assert.Equal(2, await repository.SaveAsync(run, 1));
        var completedSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await materials.GetSlotAsync(slotAddress));
        Assert.Equal(SlotOccupancyStatus.Occupied, completedSlot.Aggregate.Status);
        var runTimeline = await materials.ListTimelineAsync(new ProductionMaterialTimelineQuery(
            productionUnitId: unitId,
            productionRunId: run.Id));
        var slotCompletion = Assert.Single(runTimeline, entry =>
            entry.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition
            && entry.PreviousSlotStatus == SlotOccupancyStatus.Running
            && entry.CurrentSlotStatus == SlotOccupancyStatus.Occupied
            && entry.ProductionRunId == run.Id);
        Assert.Equal(SlotOccupancyStatus.Running, slotCompletion.PreviousSlotStatus);
        Assert.Equal(SlotOccupancyStatus.Occupied, slotCompletion.CurrentSlotStatus);
        Assert.Equal(run.Id, slotCompletion.ProductionRunId);

        var unloaded = await materialService.UnloadSlotAsync(new UnloadSlotCommand(
            slotAddress,
            material,
            station,
            "operator.main",
            Now.AddSeconds(3)));
        Assert.True(unloaded.Succeeded, unloaded.Message);
        Assert.Equal(
            SlotOccupancyStatus.Available,
            (await materials.GetSlotAsync(slotAddress))!.Aggregate.Status);
        Assert.Equal(3, await repository.SaveAsync(run, 2));
        Assert.Equal(
            SlotOccupancyStatus.Available,
            (await materials.GetSlotAsync(slotAddress))!.Aggregate.Status);
    }

    [Fact]
    public async Task ResourceLeasesAreExclusiveAndFencingTokensIncreaseAfterRelease()
    {
        var leases = new InMemoryResourceLeaseRepository();
        var resource = new ResourceRequirement(ResourceKind.Station, "station.main");
        var firstRun = ProductionRunId.New();
        var secondRun = ProductionRunId.New();

        var first = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await leases.TryAcquireAsync(
                firstRun,
                "operation@0001",
                [resource],
                Now,
                TimeSpan.FromMinutes(1))));
        Assert.Null(await leases.TryAcquireAsync(
            secondRun,
            "operation@0001",
            [resource],
            Now.AddSeconds(1),
            TimeSpan.FromMinutes(1)));

        await leases.ReleaseAsync(firstRun, "operation@0001");
        var second = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await leases.TryAcquireAsync(
                secondRun,
                "operation@0001",
                [resource],
                Now.AddSeconds(2),
                TimeSpan.FromMinutes(1))));
        Assert.True(second.FencingToken > first.FencingToken);
    }

    [Fact]
    public async Task ReplacedResourceFenceRejectsTheOldCommandEvidence()
    {
        var leases = new InMemoryResourceLeaseRepository();
        var resource = new ResourceRequirement(ResourceKind.Station, "station.main");
        var run = ProductionRunId.New();
        var first = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await leases.TryAcquireAsync(
                run,
                "operation@0001",
                [resource],
                Now,
                TimeSpan.FromMinutes(1))));
        var firstEvidence = new[] { ResourceLeaseFenceEvidence.FromLease(first) };

        Assert.True((await leases.ValidateCurrentAsync(
            run,
            "operation@0001",
            firstEvidence,
            Now.AddSeconds(1))).Accepted);
        await leases.ReleaseAsync(run, "operation@0001");
        var replacement = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await leases.TryAcquireAsync(
                run,
                "operation@0002",
                [resource],
                Now.AddSeconds(2),
                TimeSpan.FromMinutes(1))));

        Assert.False((await leases.ValidateCurrentAsync(
            run,
            "operation@0001",
            firstEvidence,
            Now.AddSeconds(3))).Accepted);
        Assert.True((await leases.ValidateCurrentAsync(
            run,
            "operation@0002",
            [ResourceLeaseFenceEvidence.FromLease(replacement)],
            Now.AddSeconds(3))).Accepted);
        Assert.True(replacement.FencingToken > first.FencingToken);
    }

    private static (ProductionRun Run, ProductionRunExecutionPlan Plan) CreateRun(
        ProductionUnitId? productionUnitId = null,
        string identityValue = "SN-001")
    {
        var runId = ProductionRunId.New();
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.main"),
            new ProcessVersionId("process-version.main"),
            []);
        var operation = new OperationExecutionPlan(
            "operation.main",
            "station.main",
            new StationId("station.main"),
            new ConfigurationSnapshotId("configuration.main"),
            new RecipeSnapshotId("recipe.main"),
            process);
        var run = ProductionRun.Create(
            runId,
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            productionUnitId ?? ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", identityValue),
            null,
            null,
            "operator.main",
            operation.Definition.OperationId,
            Now,
            [operation.Definition],
            []);
        return (run, new ProductionRunExecutionPlan(runId, [operation]));
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-runtime-{Guid.NewGuid():N}.sqlite");

        public string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        public ValueTask DisposeAsync()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return ValueTask.CompletedTask;
        }
    }
}
