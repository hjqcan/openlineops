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

public sealed class ProductionMaterialServiceTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ServiceExecutesProductionUnitAndSlotLifecycle()
    {
        var repository = new InMemoryProductionMaterialRepository();
        var service = new ProductionMaterialService(
            repository,
            new InMemoryProductionRunRepository(repository));
        var lotId = new ProductionLotId("lot-a");
        var unitId = ProductionUnitId.New();
        var material = MaterialReference.ForProductionUnit(unitId);
        var slot = new SlotAddress("line-a", "station-test", "slot-01");
        var station = MaterialLocation.AtStation(slot.LineId, slot.StationSystemId);
        var nextStation = MaterialLocation.AtStation("line-a", "station-pack");

        await AssertAccepted(service.RegisterLotAsync(new RegisterProductionLotCommand(
            lotId,
            "board-a",
            100,
            "operator-a",
            BaseTimeUtc)));
        await AssertAccepted(service.RegisterUnitAsync(new RegisterProductionUnitCommand(
            unitId,
            "board-a",
            "serial-number",
            "SN-0001",
            lotId,
            "operator-a",
            BaseTimeUtc)));
        await AssertAccepted(service.RegisterSlotAsync(new RegisterSlotCommand(
            slot,
            "engineer-a",
            BaseTimeUtc)));
        await AssertAccepted(service.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            material,
            station,
            "scanner-a",
            BaseTimeUtc.AddSeconds(1))));
        await AssertAccepted(service.ReserveSlotAsync(new ReserveSlotCommand(
            slot,
            material,
            "coordinator",
            BaseTimeUtc.AddSeconds(2))));
        await AssertAccepted(service.LoadSlotAsync(new LoadSlotCommand(
            slot,
            material,
            "operator-a",
            BaseTimeUtc.AddSeconds(3))));
        await AssertAccepted(service.StartSlotAsync(new StartSlotCommand(
            slot,
            material,
            "agent-a",
            BaseTimeUtc.AddSeconds(4))));

        var holdWhileRunning = await service.HoldAsync(new HoldProductionUnitCommand(
            unitId,
            "quality review",
            "operator-a",
            BaseTimeUtc.AddSeconds(5)));
        Assert.False(holdWhileRunning.Succeeded);
        Assert.Equal("Runtime.ProductionUnitRunning", holdWhileRunning.Code);

        await AssertAccepted(service.CompleteSlotAsync(new CompleteSlotCommand(
            slot,
            material,
            "agent-a",
            BaseTimeUtc.AddSeconds(5))));
        await AssertAccepted(service.HoldAsync(new HoldProductionUnitCommand(
            unitId,
            "quality review",
            "operator-a",
            BaseTimeUtc.AddSeconds(6))));
        await AssertAccepted(service.UnloadSlotAsync(new UnloadSlotCommand(
            slot,
            material,
            station,
            "operator-a",
            BaseTimeUtc.AddSeconds(7))));
        await AssertAccepted(service.ReleaseAsync(new ReleaseProductionUnitCommand(
            unitId,
            "quality-a",
            BaseTimeUtc.AddSeconds(8))));
        await AssertAccepted(service.TransferAsync(new TransferMaterialCommand(
            material,
            station,
            nextStation,
            "conveyor-a",
            BaseTimeUtc.AddSeconds(9))));
        await AssertAccepted(service.ScrapAsync(new ScrapProductionUnitCommand(
            unitId,
            "irreparable defect",
            "quality-a",
            BaseTimeUtc.AddSeconds(10))));

        var persistedUnit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await repository.GetProductionUnitAsync(unitId));
        var persistedSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await repository.GetSlotAsync(slot));
        Assert.Equal(ProductDisposition.Scrapped, persistedUnit.Aggregate.Disposition);
        Assert.Equal(nextStation, persistedUnit.Aggregate.Location);
        Assert.Equal(SlotOccupancyStatus.Available, persistedSlot.Aggregate.Status);
        Assert.Null(persistedSlot.Aggregate.Material);
        Assert.Equal(9, persistedUnit.Revision);
        Assert.Equal(5, persistedSlot.Revision);

        var timeline = await repository.ListTimelineAsync(
            ProductionMaterialTimelineQuery.StrictIntersection(productionUnitId: unitId));
        var locations = timeline
            .Where(entry => entry.Kind == ProductionMaterialEvidenceKind.LocationTransition)
            .ToArray();
        var slotTransitions = timeline
            .Where(entry => entry.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition)
            .ToArray();
        var dispositions = timeline
            .Where(entry => entry.Kind == ProductionMaterialEvidenceKind.DispositionTransition)
            .ToArray();
        Assert.Equal(4, locations.Length);
        Assert.Equal(station, locations[0].DestinationLocation);
        Assert.Equal(MaterialLocation.InSlot(slot), locations[1].DestinationLocation);
        Assert.Equal(station, locations[2].DestinationLocation);
        Assert.Equal(nextStation, locations[3].DestinationLocation);
        Assert.Equal(
            [
                SlotOccupancyStatus.Reserved,
                SlotOccupancyStatus.Occupied,
                SlotOccupancyStatus.Running,
                SlotOccupancyStatus.Occupied,
                SlotOccupancyStatus.Available
            ],
            slotTransitions.Select(entry => entry.CurrentSlotStatus!.Value).ToArray());
        Assert.Equal(
            [ProductDisposition.Held, ProductDisposition.InProcess, ProductDisposition.Scrapped],
            dispositions.Select(entry => entry.CurrentDisposition!.Value).ToArray());
        Assert.All(timeline, entry => Assert.False(string.IsNullOrWhiteSpace(entry.ActorId)));
    }

    [Fact]
    public async Task PostTerminalMaterialEventsRetainTheLastProductionRunContext()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var runs = new InMemoryProductionRunRepository(materials);
        var service = new ProductionMaterialService(materials, runs);
        var unitId = ProductionUnitId.New();
        var material = MaterialReference.ForProductionUnit(unitId);
        var slot = new SlotAddress("line-last-run", "station-test", "slot-01");
        var station = MaterialLocation.AtStation(slot.LineId, slot.StationSystemId);
        var downstream = MaterialLocation.AtStation(slot.LineId, "station-pack");

        await RegisterUnit(service, unitId, "SN-LAST-RUN-SLOT");
        await AssertAccepted(service.RegisterSlotAsync(new RegisterSlotCommand(
            slot,
            "engineer-a",
            BaseTimeUtc)));
        await AssertAccepted(service.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            material,
            station,
            "scanner-a",
            BaseTimeUtc.AddSeconds(1))));
        await AssertAccepted(service.ReserveSlotAsync(new ReserveSlotCommand(
            slot,
            material,
            "coordinator-a",
            BaseTimeUtc.AddSeconds(2))));
        await AssertAccepted(service.LoadSlotAsync(new LoadSlotCommand(
            slot,
            material,
            "operator-a",
            BaseTimeUtc.AddSeconds(3))));
        await AssertAccepted(service.StartSlotAsync(new StartSlotCommand(
            slot,
            material,
            "agent-a",
            BaseTimeUtc.AddSeconds(4))));

        var passedRun = await CompleteServiceRunAsync(
            materials,
            runs,
            unitId,
            "SN-LAST-RUN-SLOT",
            ResultJudgement.Passed,
            BaseTimeUtc.AddSeconds(5),
            slot);
        var terminalUnit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await materials.GetProductionUnitAsync(unitId));
        Assert.Null(terminalUnit.Aggregate.ActiveProductionRunId);
        Assert.Equal(passedRun.Id, terminalUnit.Aggregate.LastProductionRunId);
        await AssertAccepted(service.UnloadSlotAsync(new UnloadSlotCommand(
            slot,
            material,
            station,
            "operator-a",
            BaseTimeUtc.AddSeconds(8))));
        await AssertAccepted(service.TransferAsync(new TransferMaterialCommand(
            material,
            station,
            downstream,
            "conveyor-a",
            BaseTimeUtc.AddSeconds(9))));

        var passedTimeline = await materials.ListTimelineAsync(
            ProductionMaterialTimelineQuery.StrictIntersection(
                productionUnitId: unitId,
                productionRunId: passedRun.Id));
        Assert.Contains(passedTimeline, entry =>
            entry.Kind == ProductionMaterialEvidenceKind.LocationTransition
            && entry.SourceLocation == MaterialLocation.InSlot(slot)
            && entry.DestinationLocation == station
            && entry.ProductionRunId == passedRun.Id);
        Assert.Contains(passedTimeline, entry =>
            entry.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition
            && entry.PreviousSlotStatus == SlotOccupancyStatus.Occupied
            && entry.CurrentSlotStatus == SlotOccupancyStatus.Available
            && entry.ProductionRunId == passedRun.Id);
        Assert.Contains(passedTimeline, entry =>
            entry.Kind == ProductionMaterialEvidenceKind.LocationTransition
            && entry.SourceLocation == station
            && entry.DestinationLocation == downstream
            && entry.ProductionRunId == passedRun.Id);

        var dispositionUnitId = ProductionUnitId.New();
        await RegisterUnit(service, dispositionUnitId, "SN-LAST-RUN-DISPOSITION");
        await AssertAccepted(service.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            MaterialReference.ForProductionUnit(dispositionUnitId),
            station,
            "scanner-a",
            BaseTimeUtc.AddSeconds(1))));
        var failedRun = await CompleteServiceRunAsync(
            materials,
            runs,
            dispositionUnitId,
            "SN-LAST-RUN-DISPOSITION",
            ResultJudgement.Failed,
            BaseTimeUtc.AddSeconds(2));
        await AssertAccepted(service.HoldAsync(new HoldProductionUnitCommand(
            dispositionUnitId,
            "post-run review",
            "quality-a",
            BaseTimeUtc.AddSeconds(5))));
        await AssertAccepted(service.ScrapAsync(new ScrapProductionUnitCommand(
            dispositionUnitId,
            "post-run rejection",
            "quality-a",
            BaseTimeUtc.AddSeconds(6))));
        var dispositionTimeline = await materials.ListTimelineAsync(
            ProductionMaterialTimelineQuery.StrictIntersection(
                productionUnitId: dispositionUnitId,
                productionRunId: failedRun.Id));
        Assert.Equal(3, dispositionTimeline.Count(entry =>
            entry.Kind == ProductionMaterialEvidenceKind.DispositionTransition
            && entry.ProductionRunId == failedRun.Id));

        var arrivalUnitId = ProductionUnitId.New();
        await RegisterUnit(service, arrivalUnitId, "SN-LAST-RUN-ARRIVAL");
        var arrivalRun = await CompleteServiceRunAsync(
            materials,
            runs,
            arrivalUnitId,
            "SN-LAST-RUN-ARRIVAL",
            ResultJudgement.Failed,
            BaseTimeUtc.AddSeconds(1));
        await AssertAccepted(service.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            MaterialReference.ForProductionUnit(arrivalUnitId),
            station,
            "scanner-a",
            BaseTimeUtc.AddSeconds(4))));
        var arrivalTimeline = await materials.ListTimelineAsync(
            ProductionMaterialTimelineQuery.StrictIntersection(
                productionUnitId: arrivalUnitId,
                productionRunId: arrivalRun.Id));
        Assert.Contains(arrivalTimeline, entry =>
            entry.Kind == ProductionMaterialEvidenceKind.LocationTransition
            && entry.SourceLocation is null
            && entry.DestinationLocation == station
            && entry.ProductionRunId == arrivalRun.Id);
    }

    [Fact]
    public async Task ReservationRequiresMaterialAtExactStation()
    {
        var repository = new InMemoryProductionMaterialRepository();
        var service = new ProductionMaterialService(
            repository,
            new InMemoryProductionRunRepository(repository));
        var unitId = ProductionUnitId.New();
        var slot = new SlotAddress("line-a", "station-b", "slot-01");
        await AssertAccepted(service.RegisterUnitAsync(new RegisterProductionUnitCommand(
            unitId,
            "board-a",
            "serial-number",
            "SN-0002",
            null,
            "operator-a",
            BaseTimeUtc)));
        await AssertAccepted(service.RegisterSlotAsync(new RegisterSlotCommand(
            slot,
            "engineer-a",
            BaseTimeUtc)));
        await AssertAccepted(service.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            MaterialReference.ForProductionUnit(unitId),
            MaterialLocation.AtStation("line-a", "station-a"),
            "scanner-a",
            BaseTimeUtc.AddSeconds(1))));

        var result = await service.ReserveSlotAsync(new ReserveSlotCommand(
            slot,
            MaterialReference.ForProductionUnit(unitId),
            "coordinator",
            BaseTimeUtc.AddSeconds(2)));

        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.MaterialNotAtStation", result.Code);
        var persistedSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await repository.GetSlotAsync(slot));
        Assert.Equal(SlotOccupancyStatus.Available, persistedSlot.Aggregate.Status);
        Assert.Equal(0, persistedSlot.Revision);
    }

    [Fact]
    public async Task GenealogyServiceRejectsTransitiveCycle()
    {
        var repository = new InMemoryProductionMaterialRepository();
        var service = new ProductionMaterialService(
            repository,
            new InMemoryProductionRunRepository(repository));
        var first = ProductionUnitId.New();
        var second = ProductionUnitId.New();
        var third = ProductionUnitId.New();
        await RegisterUnit(service, first, "SN-A");
        await RegisterUnit(service, second, "SN-B");
        await RegisterUnit(service, third, "SN-C");

        await AssertAccepted(service.LinkGenealogyAsync(Link(first, second, "operation-1")));
        await AssertAccepted(service.LinkGenealogyAsync(Link(second, third, "operation-2")));

        var result = await service.LinkGenealogyAsync(Link(third, first, "operation-3"));

        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.MaterialGenealogyCycle", result.Code);
        Assert.Equal(2, (await repository.ListGenealogyLinksAsync()).Count);
    }

    [Fact]
    public async Task RegistrationRequiresLotWithSameProductModel()
    {
        var repository = new InMemoryProductionMaterialRepository();
        var service = new ProductionMaterialService(
            repository,
            new InMemoryProductionRunRepository(repository));
        var lotId = new ProductionLotId("lot-model-a");
        await AssertAccepted(service.RegisterLotAsync(new RegisterProductionLotCommand(
            lotId,
            "model-a",
            null,
            "operator-a",
            BaseTimeUtc)));

        var result = await service.RegisterUnitAsync(new RegisterProductionUnitCommand(
            ProductionUnitId.New(),
            "model-b",
            "serial-number",
            "SN-WRONG-MODEL",
            lotId,
            "operator-a",
            BaseTimeUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.ProductionLotModelMismatch", result.Code);
        Assert.Empty(await repository.ListProductionUnitsAsync());
    }

    [Fact]
    public async Task CarrierPositionsAreUniqueAndCapacityIsFencedByCarrierRevision()
    {
        var repository = new InMemoryProductionMaterialRepository();
        var service = new ProductionMaterialService(
            repository,
            new InMemoryProductionRunRepository(repository));
        var first = ProductionUnitId.New();
        var second = ProductionUnitId.New();
        var carrierId = new CarrierId("carrier-capacity-one");
        var station = MaterialLocation.AtStation("line-a", "station-load");
        await RegisterUnit(service, first, "SN-CARRIER-A");
        await RegisterUnit(service, second, "SN-CARRIER-B");
        await AssertAccepted(service.RegisterCarrierAsync(new RegisterCarrierCommand(
            carrierId,
            "single-position-fixture",
            1,
            "operator-a",
            BaseTimeUtc)));
        await AssertAccepted(service.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            MaterialReference.ForProductionUnit(first),
            station,
            "scanner-a",
            BaseTimeUtc.AddSeconds(1))));
        await AssertAccepted(service.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            MaterialReference.ForProductionUnit(second),
            station,
            "scanner-a",
            BaseTimeUtc.AddSeconds(1))));
        await AssertAccepted(service.TransferAsync(new TransferMaterialCommand(
            MaterialReference.ForProductionUnit(first),
            station,
            MaterialLocation.OnCarrier(carrierId, "position-01"),
            "operator-a",
            BaseTimeUtc.AddSeconds(2))));

        var occupied = await service.TransferAsync(new TransferMaterialCommand(
            MaterialReference.ForProductionUnit(second),
            station,
            MaterialLocation.OnCarrier(carrierId, "position-01"),
            "operator-a",
            BaseTimeUtc.AddSeconds(3)));
        var full = await service.TransferAsync(new TransferMaterialCommand(
            MaterialReference.ForProductionUnit(second),
            station,
            MaterialLocation.OnCarrier(carrierId, "position-02"),
            "operator-a",
            BaseTimeUtc.AddSeconds(3)));

        Assert.False(occupied.Succeeded);
        Assert.Equal("Runtime.CarrierPositionOccupied", occupied.Code);
        Assert.False(full.Succeeded);
        Assert.Equal("Runtime.CarrierCapacityExceeded", full.Code);
        var carrier = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
            await repository.GetCarrierAsync(carrierId));
        Assert.Equal(1, carrier.Revision);
    }

    private static async Task RegisterUnit(
        ProductionMaterialService service,
        ProductionUnitId unitId,
        string serialNumber)
    {
        await AssertAccepted(service.RegisterUnitAsync(new RegisterProductionUnitCommand(
            unitId,
            "board-a",
            "serial-number",
            serialNumber,
            null,
            "operator-a",
            BaseTimeUtc)));
    }

    private static async Task<ProductionRun> CompleteServiceRunAsync(
        InMemoryProductionMaterialRepository materials,
        InMemoryProductionRunRepository runs,
        ProductionUnitId unitId,
        string identityValue,
        ResultJudgement judgement,
        DateTimeOffset createdAtUtc,
        SlotAddress? slot = null)
    {
        var runId = ProductionRunId.New();
        var stationSystemId = slot?.StationSystemId ?? "station-test";
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.last-run-context"),
            new ProcessVersionId("process-version.last-run-context"),
            []);
        var operationPlan = new OperationExecutionPlan(
            "operation.last-run-context",
            stationSystemId,
            new StationId(stationSystemId),
            new ConfigurationSnapshotId("configuration.last-run-context"),
            new RecipeSnapshotId("recipe.last-run-context"),
            process,
            []);
        var run = ProductionRun.Create(
            runId,
            "project.last-run-context",
            "application.last-run-context",
            "snapshot.last-run-context",
            "topology.last-run-context",
            slot?.LineId ?? "line-last-run",
            unitId,
            new ProductionUnitIdentity("board-a", "serial-number", identityValue),
            null,
            null,
            "operator-a",
            operationPlan.Definition.OperationId,
            createdAtUtc,
            [operationPlan.Definition],
            [
                new RouteTransitionDefinition(
                    "route.last-run-failed",
                    operationPlan.Definition.OperationId,
                    null,
                    RuntimeRouteTransitionKind.Judgement,
                    ResultJudgement.Failed,
                    terminalDisposition: ProductDisposition.Nonconforming),
                new RouteTransitionDefinition(
                    "route.last-run-default",
                    operationPlan.Definition.OperationId,
                    null,
                    RuntimeRouteTransitionKind.Sequence,
                    terminalDisposition: ProductDisposition.Completed)
            ]);
        var unit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await materials.GetProductionUnitAsync(unitId));
        Assert.True(await runs.TryAddAsync(
            run,
            new ProductionRunExecutionPlan(runId, [operationPlan]),
            new ProductionRunAdmission(unit.Aggregate.ToSnapshot(), unit.Revision)));
        Assert.True(run.Start(createdAtUtc.AddMilliseconds(1)).Succeeded);
        var operation = Assert.Single(run.Operations);
        var stationResource = Assert.Single(operation.ResourceRequirements);
        var leases = new List<ResourceLease>
        {
            new(
                stationResource,
                run.Id,
                operation.OperationRunId,
                1,
                createdAtUtc.AddMilliseconds(2),
                createdAtUtc.AddMinutes(1))
        };
        if (slot is not null)
        {
            leases.Add(new ResourceLease(
                new ResourceRequirement(ResourceKind.Slot, slot.ToString()),
                run.Id,
                operation.OperationRunId,
                2,
                createdAtUtc.AddMilliseconds(2),
                createdAtUtc.AddMinutes(1)));
        }

        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            createdAtUtc.AddMilliseconds(2)).Succeeded);
        Assert.Equal(1, await runs.SaveAsync(run, 0));
        var completedAtUtc = createdAtUtc.AddSeconds(2);
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            judgement,
            null,
            1,
            1,
            0,
            completedAtUtc,
            ProductionRunExecutionEvidenceTestFactory.Create(
                run,
                operation.OperationRunId,
                ExecutionStatus.Completed,
                judgement,
                completedAtUtc,
                1,
                1)).Succeeded);
        Assert.Equal(2, await runs.SaveAsync(run, 1));
        Assert.True(run.IsTerminal);
        return run;
    }

    private static LinkMaterialGenealogyCommand Link(
        ProductionUnitId parent,
        ProductionUnitId child,
        string operationId)
    {
        return new LinkMaterialGenealogyCommand(
            MaterialGenealogyLinkId.New(),
            parent,
            child,
            "ComponentOf",
            operationId,
            "operator-a",
            BaseTimeUtc.AddMinutes(1));
    }

    private static async Task AssertAccepted(ValueTask<OpenLineOps.Runtime.Domain.Operations.RuntimeOperationResult> task)
    {
        var result = await task;
        Assert.True(result.Succeeded, result.Message);
    }
}
