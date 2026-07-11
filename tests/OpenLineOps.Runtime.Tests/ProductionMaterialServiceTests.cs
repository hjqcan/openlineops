using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
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
            new ProductionMaterialTimelineQuery(productionUnitId: unitId));
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
