using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunActiveQueryRepositoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryRepositoryFiltersOnlyCurrentOperationsByCanonicalResource()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);

        await AssertActiveQueryContractAsync(repository, materials);
    }

    [Fact]
    public async Task SqliteRepositoryFiltersOnlyCurrentOperationsByCanonicalResource()
    {
        await using var database = new TemporaryDatabase();
        using var materials = new SqliteProductionMaterialRepository(database.ConnectionString);
        using var repository = new SqliteProductionRunRepository(database.ConnectionString);

        await AssertActiveQueryContractAsync(repository, materials);
    }

    [Fact]
    public void QueryNormalizesSlotScopeAndRejectsNoncanonicalOrContradictoryResourceScope()
    {
        var normalized = new ProductionRunActiveQuery(
            slotResourceId: "line.query/station.query/slot.shared");
        Assert.Equal("line.query", normalized.ProductionLineDefinitionId);
        Assert.Equal("station.query", normalized.StationSystemId);
        Assert.Equal(
            "line.query/station.query/slot.shared",
            normalized.SlotResourceId);

        Assert.Throws<ArgumentException>(() =>
            new ProductionRunActiveQuery(productionLineDefinitionId: " line.query"));
        Assert.Throws<ArgumentException>(() =>
            new ProductionRunActiveQuery(stationSystemId: "station.query "));
        Assert.Throws<ArgumentException>(() =>
            new ProductionRunActiveQuery(slotResourceId: "slot.shared"));
        Assert.Throws<ArgumentException>(() =>
            new ProductionRunActiveQuery(slotResourceId: "line.query/ station.query/slot.shared"));
        Assert.Throws<ArgumentException>(() =>
            new ProductionRunActiveQuery(
                productionLineDefinitionId: "line.other",
                slotResourceId: "line.query/station.query/slot.shared"));
        Assert.Throws<ArgumentException>(() =>
            new ProductionRunActiveQuery(
                stationSystemId: "station.other",
                slotResourceId: "line.query/station.query/slot.shared"));
    }

    private static async Task AssertActiveQueryContractAsync(
        IProductionRunRepository repository,
        IProductionMaterialRepository materials)
    {
        const string lineId = "line.query";
        const string localSlotId = "slot.shared";
        var currentPlan = OperationPlan(
            "operation.current",
            lineId,
            "station.current",
            localSlotId);
        var futurePlan = OperationPlan(
            "operation.future",
            lineId,
            "station.future",
            localSlotId);
        var sequential = CreateRun(
            lineId,
            "SN-QUERY-SEQUENTIAL",
            [currentPlan, futurePlan],
            [
                new RouteTransitionDefinition(
                    "route.current-future",
                    currentPlan.Definition.OperationId,
                    futurePlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.Sequence),
                Terminal("route.future-completed", futurePlan.Definition.OperationId)
            ]);
        var currentSlot = SlotResourceId(lineId, "station.current", localSlotId);
        var futureSlot = SlotResourceId(lineId, "station.future", localSlotId);
        await AddAsync(
            repository,
            materials,
            sequential,
            [currentPlan, futurePlan],
            new SlotAddress(lineId, "station.current", localSlotId),
            [new SlotAddress(lineId, "station.future", localSlotId)]);
        await AssertIncludesAsync(
            repository,
            sequential.Id,
            new ProductionRunActiveQuery(
                lineId,
                "station.current",
                currentSlot));
        await AssertExcludesAsync(
            repository,
            sequential.Id,
            new ProductionRunActiveQuery(slotResourceId: futureSlot));
        await AssertExcludesAsync(
            repository,
            sequential.Id,
            new ProductionRunActiveQuery(stationSystemId: "station.future"));

        Assert.True(sequential.Start(Now.AddSeconds(1)).Succeeded);
        Assert.Equal(1, await repository.SaveAsync(sequential, 0));
        await AssertIncludesAsync(
            repository,
            sequential.Id,
            new ProductionRunActiveQuery(slotResourceId: currentSlot));
        await AssertExcludesAsync(
            repository,
            sequential.Id,
            new ProductionRunActiveQuery(slotResourceId: futureSlot));

        StartAndComplete(
            sequential,
            "operation.current@0001",
            ResultJudgement.NotApplicable,
            Now.AddSeconds(2));
        Assert.Equal(2, await repository.SaveAsync(sequential, 1));
        await AssertExcludesAsync(
            repository,
            sequential.Id,
            new ProductionRunActiveQuery(slotResourceId: currentSlot));
        await AssertIncludesAsync(
            repository,
            sequential.Id,
            new ProductionRunActiveQuery(
                lineId,
                "station.future",
                futureSlot));

        const string parallelLineId = "line.parallel";
        var entryPlan = OperationPlan(
            "operation.entry",
            parallelLineId,
            "station.entry",
            slotId: null);
        var leftPlan = OperationPlan(
            "operation.left",
            parallelLineId,
            "station.left",
            localSlotId);
        var rightPlan = OperationPlan(
            "operation.right",
            parallelLineId,
            "station.right",
            localSlotId);
        var joinPlan = OperationPlan(
            "operation.join",
            parallelLineId,
            "station.join",
            slotId: null);
        var parallel = CreateRun(
            parallelLineId,
            "SN-QUERY-PARALLEL",
            [entryPlan, leftPlan, rightPlan, joinPlan],
            [
                new RouteTransitionDefinition(
                    "route.entry-left",
                    entryPlan.Definition.OperationId,
                    leftPlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelFork,
                    parallelGroupId: "parallel.work"),
                new RouteTransitionDefinition(
                    "route.entry-right",
                    entryPlan.Definition.OperationId,
                    rightPlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelFork,
                    parallelGroupId: "parallel.work"),
                new RouteTransitionDefinition(
                    "route.left-join",
                    leftPlan.Definition.OperationId,
                    joinPlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelJoin,
                    parallelGroupId: "parallel.work"),
                new RouteTransitionDefinition(
                    "route.right-join",
                    rightPlan.Definition.OperationId,
                    joinPlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelJoin,
                    parallelGroupId: "parallel.work"),
                Terminal("route.join-completed", joinPlan.Definition.OperationId)
            ]);
        await AddAsync(
            repository,
            materials,
            parallel,
            [entryPlan, leftPlan, rightPlan, joinPlan],
            runningSlot: null,
            [
                new SlotAddress(parallelLineId, "station.left", localSlotId),
                new SlotAddress(parallelLineId, "station.right", localSlotId)
            ]);
        Assert.True(parallel.Start(Now.AddMinutes(1)).Succeeded);
        StartAndComplete(
            parallel,
            "operation.entry@0001",
            ResultJudgement.NotApplicable,
            Now.AddMinutes(1).AddSeconds(1));
        Assert.Equal(1, await repository.SaveAsync(parallel, 0));

        await AssertIncludesAsync(
            repository,
            parallel.Id,
            new ProductionRunActiveQuery(
                parallelLineId,
                "station.left",
                SlotResourceId(parallelLineId, "station.left", localSlotId)));
        await AssertIncludesAsync(
            repository,
            parallel.Id,
            new ProductionRunActiveQuery(
                parallelLineId,
                "station.right",
                SlotResourceId(parallelLineId, "station.right", localSlotId)));
        await AssertExcludesAsync(
            repository,
            parallel.Id,
            new ProductionRunActiveQuery(stationSystemId: "station.join"));

        await AssertDynamicSlotContractAsync(
            repository,
            materials,
            MaterialSlotResolution.CurrentMaterialSlot,
            "current-material-slot",
            Now.AddMinutes(2));
        await AssertDynamicSlotContractAsync(
            repository,
            materials,
            MaterialSlotResolution.AvailableSlotInGroup,
            "available-slot-in-group",
            Now.AddMinutes(3));

        var tiedRuns = await AddTiedPendingRunsAsync(repository, materials);
        var ordered = await repository.ListActiveAsync(ProductionRunActiveQuery.All);
        Assert.Equal(
            ordered
                .OrderByDescending(static entry => entry.Run.LastTransitionAtUtc)
                .ThenBy(static entry => entry.Run.Id.Value)
                .Select(static entry => entry.Run.Id),
            ordered.Select(static entry => entry.Run.Id));
        var tiedIds = tiedRuns.Select(static run => run.Id).ToHashSet();
        Assert.Equal(
            tiedIds.OrderBy(static id => id.Value),
            ordered
                .Select(static entry => entry.Run.Id)
                .Where(tiedIds.Contains));
    }

    private static async Task<ProductionRun[]> AddTiedPendingRunsAsync(
        IProductionRunRepository repository,
        IProductionMaterialRepository materials)
    {
        var runs = new List<ProductionRun>();
        foreach (var suffix in new[] { "tie-a", "tie-b" })
        {
            var lineId = $"line.order.{suffix}";
            var operation = OperationPlan(
                $"operation.order.{suffix}",
                lineId,
                $"station.order.{suffix}",
                slotId: null);
            var run = CreateRun(
                lineId,
                $"SN-QUERY-{suffix}",
                [operation],
                [Terminal($"route.order.{suffix}.completed", operation.Definition.OperationId)]);
            await AddAsync(
                repository,
                materials,
                run,
                [operation],
                runningSlot: null,
                []);
            runs.Add(run);
        }

        return runs.ToArray();
    }

    private static async Task AssertDynamicSlotContractAsync(
        IProductionRunRepository repository,
        IProductionMaterialRepository materials,
        MaterialSlotResolution resolution,
        string suffix,
        DateTimeOffset startedAtUtc)
    {
        var lineId = $"line.dynamic.{suffix}";
        var stationSystemId = $"station.dynamic.{suffix}";
        var boundSlotId = $"slot.bound.{suffix}";
        var otherSlotId = $"slot.other.{suffix}";
        var boundSlot = new SlotAddress(lineId, stationSystemId, boundSlotId);
        var otherSlot = new SlotAddress(lineId, stationSystemId, otherSlotId);
        var materialSlotRequirement = resolution == MaterialSlotResolution.CurrentMaterialSlot
            ? new MaterialSlotRequirement(resolution, stationSystemId)
            : new MaterialSlotRequirement(
                resolution,
                $"group.dynamic.{suffix}",
                [boundSlotId, otherSlotId]);
        var operation = OperationPlan(
            $"operation.dynamic.{suffix}",
            lineId,
            stationSystemId,
            slotId: null,
            materialSlotRequirement);
        var run = CreateRun(
            lineId,
            $"SN-QUERY-{suffix}",
            [operation],
            [Terminal($"route.dynamic.{suffix}.completed", operation.Definition.OperationId)]);
        await AddAsync(
            repository,
            materials,
            run,
            [operation],
            boundSlot,
            [otherSlot]);
        var boundSlotQuery = new ProductionRunActiveQuery(
            lineId,
            stationSystemId,
            boundSlot.ToString());
        var otherSlotQuery = new ProductionRunActiveQuery(
            lineId,
            stationSystemId,
            otherSlot.ToString());

        await AssertExcludesAsync(repository, run.Id, boundSlotQuery);
        await AssertExcludesAsync(repository, run.Id, otherSlotQuery);

        Assert.True(run.Start(startedAtUtc).Succeeded);
        Assert.Equal(1, await repository.SaveAsync(run, 0));
        await AssertExcludesAsync(repository, run.Id, boundSlotQuery);
        await AssertExcludesAsync(repository, run.Id, otherSlotQuery);

        var operationSnapshot = Assert.Single(run.ToSnapshot().Operations);
        var readiness = await new ProductionOperationReadinessEvaluator(
                materials,
                LegacyCompatibilityStationProductionExecutionGate.Instance)
            .EvaluateAsync(run.ToSnapshot(), operationSnapshot);
        Assert.Equal(ProductionOperationReadinessKind.Ready, readiness.Kind);
        Assert.Equal(
            new ResourceRequirement(ResourceKind.Slot, boundSlot.ToString()),
            Assert.Single(readiness.MaterialResources));
        var resources = operationSnapshot.Definition.ResourceRequirements
            .Concat(readiness.MaterialResources)
            .Distinct()
            .ToArray();
        var leases = resources
            .Select((resource, index) => new ResourceLease(
                resource,
                run.Id,
                operationSnapshot.OperationRunId,
                index + 1,
                startedAtUtc,
                startedAtUtc.AddMinutes(5)))
            .ToArray();
        Assert.True(run.StartOperation(
            operationSnapshot.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            startedAtUtc.AddSeconds(1)).Succeeded);
        Assert.Equal(2, await repository.SaveAsync(run, 1));

        await AssertIncludesAsync(repository, run.Id, boundSlotQuery);
        await AssertExcludesAsync(repository, run.Id, otherSlotQuery);
    }

    private static async Task AddAsync(
        IProductionRunRepository repository,
        IProductionMaterialRepository materials,
        ProductionRun run,
        IReadOnlyList<OperationExecutionPlan> operations,
        SlotAddress? runningSlot,
        IReadOnlyCollection<SlotAddress> additionalSlots)
    {
        Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            null,
            run.ActorId,
            run.CreatedAtUtc.AddSeconds(-20))));
        foreach (var slot in additionalSlots
                     .Prepend(runningSlot)
                     .OfType<SlotAddress>())
        {
            Assert.True(await materials.TryAddAsync(SlotOccupancy.Register(
                slot,
                run.CreatedAtUtc.AddSeconds(-10))));
        }

        if (runningSlot is not null)
        {
            var material = MaterialReference.ForProductionUnit(run.ProductionUnitId);
            var materialService = new ProductionMaterialService(materials, repository);
            Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
                Guid.NewGuid(),
                material,
                MaterialLocation.AtStation(
                    runningSlot.LineId,
                    runningSlot.StationSystemId),
                "scanner.query",
                run.CreatedAtUtc.AddSeconds(-8)))).Succeeded);
            Assert.True((await materialService.ReserveSlotAsync(new ReserveSlotCommand(
                runningSlot,
                material,
                "coordinator.query",
                run.CreatedAtUtc.AddSeconds(-7)))).Succeeded);
            Assert.True((await materialService.LoadSlotAsync(new LoadSlotCommand(
                runningSlot,
                material,
                "operator.query",
                run.CreatedAtUtc.AddSeconds(-6)))).Succeeded);
            Assert.True((await materialService.StartSlotAsync(new StartSlotCommand(
                runningSlot,
                material,
                "agent.query",
                run.CreatedAtUtc.AddSeconds(-5)))).Succeeded);
        }

        var unit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await materials.GetProductionUnitAsync(run.ProductionUnitId));
        Assert.True(await repository.TryAddAsync(
            run,
            new ProductionRunExecutionPlan(run.Id, operations),
            new ProductionRunAdmission(unit.Aggregate.ToSnapshot(), unit.Revision)));
    }

    private static async Task AssertIncludesAsync(
        IProductionRunRepository repository,
        ProductionRunId expectedRunId,
        ProductionRunActiveQuery query)
    {
        var matches = await repository.ListActiveAsync(query);
        Assert.Contains(matches, entry => entry.Run.Id == expectedRunId);
    }

    private static async Task AssertExcludesAsync(
        IProductionRunRepository repository,
        ProductionRunId excludedRunId,
        ProductionRunActiveQuery query)
    {
        var matches = await repository.ListActiveAsync(query);
        Assert.DoesNotContain(matches, entry => entry.Run.Id == excludedRunId);
    }

    private static ProductionRun CreateRun(
        string lineId,
        string identityValue,
        IReadOnlyList<OperationExecutionPlan> operations,
        IReadOnlyCollection<RouteTransitionDefinition> transitions)
    {
        var entry = operations[0];
        return ProductionRun.Create(
            ProductionRunId.New(),
            $"project.{lineId}",
            $"application.{lineId}",
            $"snapshot.{lineId}",
            $"topology.{lineId}",
            lineId,
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", identityValue),
            null,
            null,
            "operator.query",
            entry.Definition.OperationId,
            Now,
            operations.Select(static operation => operation.Definition),
            transitions);
    }

    private static OperationExecutionPlan OperationPlan(
        string operationId,
        string lineId,
        string stationSystemId,
        string? slotId,
        MaterialSlotRequirement? materialSlotRequirement = null)
    {
        var resources = new List<ResourceRequirement>
        {
            new(ResourceKind.Station, stationSystemId)
        };
        if (slotId is not null)
        {
            resources.Add(new ResourceRequirement(
                ResourceKind.Slot,
                SlotResourceId(lineId, stationSystemId, slotId)));
        }

        return new OperationExecutionPlan(
            operationId,
            stationSystemId,
            new StationId(stationSystemId),
            new ConfigurationSnapshotId($"configuration.{operationId}"),
            new RecipeSnapshotId($"recipe.{operationId}"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId($"process.{operationId}"),
                new ProcessVersionId($"process-version.{operationId}"),
                []),
            [],
            resources,
            materialSlotRequirement);
    }

    private static RouteTransitionDefinition Terminal(string id, string operationId) => new(
        id,
        operationId,
        null,
        RuntimeRouteTransitionKind.Sequence,
        terminalDisposition: ProductDisposition.Completed);

    private static string SlotResourceId(
        string lineId,
        string stationSystemId,
        string slotId) =>
        $"{lineId}/{stationSystemId}/{slotId}";

    private static void StartAndComplete(
        ProductionRun run,
        string operationRunId,
        ResultJudgement judgement,
        DateTimeOffset startedAtUtc)
    {
        var operation = run.Operations.Single(candidate =>
            string.Equals(
                candidate.OperationRunId,
                operationRunId,
                StringComparison.Ordinal));
        var leases = operation.ResourceRequirements
            .Select((resource, index) => new ResourceLease(
                resource,
                run.Id,
                operationRunId,
                index + 1,
                startedAtUtc,
                startedAtUtc.AddMinutes(5)))
            .ToArray();
        Assert.True(run.StartOperation(
            operationRunId,
            RuntimeSessionId.New(),
            leases,
            startedAtUtc).Succeeded);
        var completedAtUtc = startedAtUtc.AddSeconds(1);
        Assert.True(run.CompleteOperation(
            operationRunId,
            judgement,
            null,
            0,
            0,
            0,
            completedAtUtc,
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                operationRunId,
                ExecutionStatus.Completed,
                judgement,
                completedAtUtc)).Succeeded);
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-active-query-{Guid.NewGuid():N}.sqlite");

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
