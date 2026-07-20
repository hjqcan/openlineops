using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Traceability.Api.RuntimeIntegration;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresProductionMaterialRepositoryIntegrationTests(
    PostgresContainerFixture fixture)
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    [PostgresIntegrationFact]
    public async Task RepositoryRoundTripsTheCompleteMaterialGraphAcrossRestart()
    {
        var unique = Guid.NewGuid().ToString("N");
        var lot = ProductionLot.Register(
            new ProductionLotId($"lot-{unique}"),
            "controller-board",
            12,
            "integration-test",
            BaseTimeUtc);
        var parent = CreateUnit($"PARENT-{unique}", lot.Id);
        var child = CreateUnit($"CHILD-{unique}", lot.Id);
        var carrier = Carrier.Register(
            new CarrierId($"carrier-{unique}"),
            "panel-tray",
            12,
            "integration-test",
            BaseTimeUtc);
        var occupiedAddress = new SlotAddress(
            $"line-{unique}",
            "station-test",
            "slot-01");
        var availableAddress = new SlotAddress(
            occupiedAddress.LineId,
            occupiedAddress.StationSystemId,
            "slot-02");
        var occupiedSlot = SlotOccupancy.Register(occupiedAddress, BaseTimeUtc);
        var availableSlot = SlotOccupancy.Register(availableAddress, BaseTimeUtc);
        var material = MaterialReference.ForProductionUnit(child.Id);
        var completionRunId = ProductionRunId.New();
        var stationQueue = MaterialLocation.AtStation(
            occupiedAddress.LineId,
            occupiedAddress.StationSystemId);
        var genealogy = new MaterialGenealogyLink(
            MaterialGenealogyLinkId.New(),
            parent.Id,
            child.Id,
            "ComponentOf",
            "assemble-board",
            "integration-test",
            BaseTimeUtc.AddSeconds(4));

        await using (var repository =
            new PostgreSqlProductionMaterialRepository(fixture.ConnectionString))
        {
            Assert.True(await repository.TryAddAsync(lot));
            Assert.True(await repository.TryAddAsync(parent));
            Assert.True(await repository.TryAddAsync(child));
            Assert.True(await repository.TryAddAsync(carrier));
            Assert.True(await repository.TryAddAsync(occupiedSlot));
            Assert.True(await repository.TryAddAsync(availableSlot));
            var childEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await repository.GetProductionUnitAsync(child.Id));
            var carrierEntry = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
                await repository.GetCarrierAsync(carrier.Id));
            var slotEntry = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
                await repository.GetSlotAsync(occupiedAddress));
            Assert.True(childEntry.Aggregate.Arrive(stationQueue, BaseTimeUtc.AddSeconds(1)).Succeeded);
            Assert.True(carrierEntry.Aggregate.Arrive(stationQueue, BaseTimeUtc.AddSeconds(1)).Succeeded);
            Assert.True(slotEntry.Aggregate.Reserve(material, BaseTimeUtc.AddSeconds(2)).Succeeded);
            Assert.True(slotEntry.Aggregate.Load(material, BaseTimeUtc.AddSeconds(3)).Succeeded);
            Assert.True(slotEntry.Aggregate.Start(material, BaseTimeUtc.AddSeconds(4)).Succeeded);
            Assert.True(slotEntry.Aggregate.Complete(material, BaseTimeUtc.AddSeconds(5)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                productionUnits:
                [new ProductionUnitUpdate(childEntry.Aggregate, childEntry.Revision)],
                carriers: [new CarrierUpdate(carrierEntry.Aggregate, carrierEntry.Revision)],
                slots: [new SlotOccupancyUpdate(slotEntry.Aggregate, slotEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(), material, null, null, stationQueue,
                        "integration-test", BaseTimeUtc.AddSeconds(1)),
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(), MaterialReference.ForCarrier(carrier.Id), null, null,
                        stationQueue, "integration-test", BaseTimeUtc.AddSeconds(1)),
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(), occupiedAddress, material, null,
                        null, null,
                        SlotOccupancyStatus.Available, SlotOccupancyStatus.Reserved,
                        "integration-test", BaseTimeUtc.AddSeconds(2)),
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(), occupiedAddress, material, null,
                        null, null,
                        SlotOccupancyStatus.Reserved, SlotOccupancyStatus.Occupied,
                        "integration-test", BaseTimeUtc.AddSeconds(3)),
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(), occupiedAddress, material, null,
                        null, null,
                        SlotOccupancyStatus.Occupied, SlotOccupancyStatus.Running,
                        "integration-test", BaseTimeUtc.AddSeconds(4)),
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(), occupiedAddress, material, completionRunId,
                        "operation.test@0001", 73,
                        SlotOccupancyStatus.Running, SlotOccupancyStatus.Occupied,
                        "coordinator.main", BaseTimeUtc.AddSeconds(5))
                ]));
            Assert.True(await repository.TryAddAsync(genealogy));
        }

        await using var restarted =
            new PostgreSqlProductionMaterialRepository(fixture.ConnectionString);
        var restoredLot = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionLot>>(
            await restarted.GetProductionLotAsync(lot.Id));
        var restoredChild = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await restarted.GetProductionUnitAsync(child.Id));
        var restoredCarrier = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
            await restarted.GetCarrierAsync(carrier.Id));
        var listedCarrier = Assert.Single(
            await restarted.ListCarriersAsync(),
            entry => entry.Aggregate.Id == carrier.Id);
        var restoredSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await restarted.GetSlotAsync(occupiedAddress));
        var lineSlots = await restarted.ListSlotsAsync(occupiedAddress.LineId);
        var stationSlots = await restarted.ListSlotsAsync(
            occupiedAddress.LineId,
            occupiedAddress.StationSystemId);
        var restoredLink = Assert.Single(
            await restarted.ListGenealogyLinksAsync(),
            link => link.Id == genealogy.Id);
        var completionEvidence = Assert.Single(await restarted.ListTimelineAsync(
            ProductionMaterialTimelineQuery.StrictIntersection(
                productionUnitId: child.Id,
                productionRunId: completionRunId)));

        Assert.Equal(12, restoredLot.Aggregate.DeclaredQuantity);
        Assert.Equal(lot.Id, restoredChild.Aggregate.LotId);
        Assert.Equal(stationQueue, restoredChild.Aggregate.Location);
        Assert.Equal(stationQueue, restoredCarrier.Aggregate.Location);
        Assert.Equal(carrier.Id, listedCarrier.Aggregate.Id);
        Assert.Equal(SlotOccupancyStatus.Occupied, restoredSlot.Aggregate.Status);
        Assert.Equal(material, restoredSlot.Aggregate.Material);
        Assert.Equal(genealogy, restoredLink);
        Assert.Equal("operation.test@0001", completionEvidence.OperationRunId);
        Assert.Equal(73, completionEvidence.SlotFencingToken);
        Assert.Equal(2, lineSlots.Count);
        Assert.Equal(2, stationSlots.Count);
        Assert.All(
            new[]
            {
                restoredLot.Revision,
                restoredChild.Revision,
                restoredCarrier.Revision,
                restoredSlot.Revision
            },
            revision => Assert.True(revision is 0 or 1));
        Assert.Equal(1, restoredChild.Revision);
        Assert.Equal(1, restoredCarrier.Revision);
        Assert.Equal(1, restoredSlot.Revision);
    }

    [PostgresIntegrationFact]
    public async Task ColdReadPreservesTwoUnitsAtDistinctCarrierPositions()
    {
        await using var schema = await PostgresIsolatedSchema.CreateAsync(
            fixture.ConnectionString,
            "carrierpositions");
        var unique = Guid.NewGuid().ToString("N");
        var firstUnitId = ProductionUnitId.New();
        var secondUnitId = ProductionUnitId.New();
        var carrierId = new CarrierId($"carrier-two-position-{unique}");
        var station = MaterialLocation.AtStation($"line-{unique}", "station-load");
        var firstPosition = MaterialLocation.OnCarrier(carrierId, "position-01");
        var secondPosition = MaterialLocation.OnCarrier(carrierId, "position-02");

        await using (var materials =
            new PostgreSqlProductionMaterialRepository(schema.ConnectionString))
        {
            using var runs = new PostgreSqlProductionCoordinationStore(schema.ConnectionString);
            var service = new ProductionMaterialService(materials, runs);
            Assert.True((await service.RegisterUnitAsync(new RegisterProductionUnitCommand(
                firstUnitId,
                "controller-board",
                "serial-number",
                $"CARRIER-POSITION-01-{unique}",
                null,
                "integration-test",
                BaseTimeUtc))).Succeeded);
            Assert.True((await service.RegisterUnitAsync(new RegisterProductionUnitCommand(
                secondUnitId,
                "controller-board",
                "serial-number",
                $"CARRIER-POSITION-02-{unique}",
                null,
                "integration-test",
                BaseTimeUtc))).Succeeded);
            Assert.True((await service.RegisterCarrierAsync(new RegisterCarrierCommand(
                carrierId,
                "panel-tray-2",
                2,
                "integration-test",
                BaseTimeUtc))).Succeeded);
            Assert.True((await service.ArriveAsync(new ArriveMaterialCommand(
                Guid.NewGuid(),
                MaterialReference.ForCarrier(carrierId),
                station,
                "scanner-a",
                BaseTimeUtc.AddSeconds(1)))).Succeeded);
            Assert.True((await service.ArriveAsync(new ArriveMaterialCommand(
                Guid.NewGuid(),
                MaterialReference.ForProductionUnit(firstUnitId),
                station,
                "scanner-a",
                BaseTimeUtc.AddSeconds(2)))).Succeeded);
            Assert.True((await service.ArriveAsync(new ArriveMaterialCommand(
                Guid.NewGuid(),
                MaterialReference.ForProductionUnit(secondUnitId),
                station,
                "scanner-a",
                BaseTimeUtc.AddSeconds(3)))).Succeeded);
            Assert.True((await service.TransferAsync(new TransferMaterialCommand(
                MaterialReference.ForProductionUnit(firstUnitId),
                station,
                firstPosition,
                "integration-test",
                BaseTimeUtc.AddSeconds(4)))).Succeeded);
            Assert.True((await service.TransferAsync(new TransferMaterialCommand(
                MaterialReference.ForProductionUnit(secondUnitId),
                station,
                secondPosition,
                "integration-test",
                BaseTimeUtc.AddSeconds(5)))).Succeeded);
        }

        await using var restarted =
            new PostgreSqlProductionMaterialRepository(schema.ConnectionString);
        var restoredUnits = (await restarted.ListProductionUnitsAsync())
            .Where(entry => entry.Aggregate.Location?.CarrierId == carrierId)
            .ToDictionary(entry => entry.Aggregate.Id);
        var restoredCarrier = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
            await restarted.GetCarrierAsync(carrierId));

        Assert.Equal(2, restoredUnits.Count);
        Assert.Equal(firstPosition, restoredUnits[firstUnitId].Aggregate.Location);
        Assert.Equal(secondPosition, restoredUnits[secondUnitId].Aggregate.Location);
        Assert.Equal(2, restoredCarrier.Aggregate.Capacity);
        Assert.Equal(station, restoredCarrier.Aggregate.Location);
        Assert.Contains(
            await restarted.ListTimelineAsync(
                ProductionMaterialTimelineQuery.StrictIntersection(
                    productionUnitId: firstUnitId)),
            entry => entry.DestinationLocation == firstPosition);
        Assert.Contains(
            await restarted.ListTimelineAsync(
                ProductionMaterialTimelineQuery.StrictIntersection(
                    productionUnitId: secondUnitId)),
            entry => entry.DestinationLocation == secondPosition);
    }

    [PostgresIntegrationFact]
    public async Task TimelineQueryModesPreserveMaterialRunAndTimeScopeSemantics()
    {
        await using var schema = await PostgresIsolatedSchema.CreateAsync(
            fixture.ConnectionString,
            "timeline");
        await using var repository =
            new PostgreSqlProductionMaterialRepository(schema.ConnectionString);
        var unique = Guid.NewGuid().ToString("N");
        var parent = CreateUnit($"TIMELINE-PARENT-{unique}");
        var child = CreateUnit($"TIMELINE-CHILD-{unique}");
        var unrelated = CreateUnit($"TIMELINE-UNRELATED-{unique}");
        var carrier = Carrier.Register(
            new CarrierId($"timeline-carrier-{unique}"),
            "timeline-tray",
            1,
            "integration-test",
            BaseTimeUtc);
        var runId = ProductionRunId.New();
        var otherRunId = ProductionRunId.New();
        var firstStation = MaterialLocation.AtStation($"timeline-line-{unique}", "station-a");
        var secondStation = MaterialLocation.AtStation($"timeline-line-{unique}", "station-b");
        var childRunEvidenceId = Guid.NewGuid();
        var carrierRunEvidenceId = Guid.NewGuid();
        var unrelatedRunEvidenceId = Guid.NewGuid();
        var genealogy = new MaterialGenealogyLink(
            MaterialGenealogyLinkId.New(),
            parent.Id,
            child.Id,
            "ComponentOf",
            "assemble-board",
            "integration-test",
            BaseTimeUtc.AddSeconds(4));
        var childOtherRunEvidenceId = Guid.NewGuid();

        Assert.True(await repository.TryAddAsync(parent));
        Assert.True(await repository.TryAddAsync(child));
        Assert.True(await repository.TryAddAsync(unrelated));
        Assert.True(await repository.TryAddAsync(carrier));
        Assert.True(child.Arrive(firstStation, BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(carrier.Arrive(firstStation, BaseTimeUtc.AddSeconds(2)).Succeeded);
        Assert.True(unrelated.Arrive(firstStation, BaseTimeUtc.AddSeconds(3)).Succeeded);
        await repository.CommitAsync(new ProductionMaterialCommit(
            productionUnits:
            [
                new ProductionUnitUpdate(child, 0),
                new ProductionUnitUpdate(unrelated, 0)
            ],
            carriers: [new CarrierUpdate(carrier, 0)],
            timeline:
            [
                ProductionMaterialTimelineEntry.Location(
                    childRunEvidenceId,
                    MaterialReference.ForProductionUnit(child.Id),
                    runId,
                    null,
                    firstStation,
                    "coordinator.main",
                    BaseTimeUtc.AddSeconds(1)),
                ProductionMaterialTimelineEntry.Location(
                    carrierRunEvidenceId,
                    MaterialReference.ForCarrier(carrier.Id),
                    runId,
                    null,
                    firstStation,
                    "coordinator.main",
                    BaseTimeUtc.AddSeconds(2)),
                ProductionMaterialTimelineEntry.Location(
                    unrelatedRunEvidenceId,
                    MaterialReference.ForProductionUnit(unrelated.Id),
                    runId,
                    null,
                    firstStation,
                    "coordinator.main",
                    BaseTimeUtc.AddSeconds(3))
            ]));
        Assert.True(await repository.TryAddAsync(genealogy));

        Assert.True(child.Transfer(
            firstStation,
            secondStation,
            BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.True(carrier.Transfer(
            firstStation,
            secondStation,
            BaseTimeUtc.AddSeconds(6)).Succeeded);
        await repository.CommitAsync(new ProductionMaterialCommit(
            productionUnits: [new ProductionUnitUpdate(child, 1)],
            carriers: [new CarrierUpdate(carrier, 1)],
            timeline:
            [
                ProductionMaterialTimelineEntry.Location(
                    childOtherRunEvidenceId,
                    MaterialReference.ForProductionUnit(child.Id),
                    otherRunId,
                    firstStation,
                    secondStation,
                    "coordinator.main",
                    BaseTimeUtc.AddSeconds(5)),
                ProductionMaterialTimelineEntry.Location(
                    Guid.NewGuid(),
                    MaterialReference.ForCarrier(carrier.Id),
                    runId,
                    firstStation,
                    secondStation,
                    "coordinator.main",
                    BaseTimeUtc.AddSeconds(6))
            ]));

        var strict = await repository.ListTimelineAsync(
            ProductionMaterialTimelineQuery.StrictIntersection(
                child.Id,
                runId,
                carrier.Id,
                BaseTimeUtc.AddSeconds(5)));
        var union = await repository.ListTimelineAsync(
            ProductionMaterialTimelineQuery.UnionScope(
                child.Id,
                runId,
                carrier.Id,
                BaseTimeUtc.AddSeconds(5)));

        Assert.Equal(
            [childRunEvidenceId, carrierRunEvidenceId],
            strict.Select(entry => entry.EvidenceId).ToArray());
        Assert.Equal(
            [
                childRunEvidenceId,
                carrierRunEvidenceId,
                unrelatedRunEvidenceId,
                genealogy.Id.Value,
                childOtherRunEvidenceId
            ],
            union.Select(entry => entry.EvidenceId).ToArray());
    }

    [PostgresIntegrationFact]
    public async Task ProductLifecycleRebuildsAfterColdRestartAndScopesCarrierEvidenceToMembership()
    {
        await using var schema = await PostgresIsolatedSchema.CreateAsync(
            fixture.ConnectionString,
            "materiallifecycle");
        var unique = Guid.NewGuid().ToString("N");
        var unit = CreateUnit($"LIFECYCLE-{unique}");
        var carrier = Carrier.Register(
            new CarrierId($"lifecycle-carrier-{unique}"),
            "lifecycle-tray",
            4,
            "integration-test",
            BaseTimeUtc);
        var carrierMaterial = MaterialReference.ForCarrier(carrier.Id);
        var unitMaterial = MaterialReference.ForProductionUnit(unit.Id);
        var lineId = $"lifecycle-line-{unique}";
        var stationA = MaterialLocation.AtStation(lineId, "station-a");
        var stationB = MaterialLocation.AtStation(lineId, "station-b");
        var stationC = MaterialLocation.AtStation(lineId, "station-c");
        var carrierPosition = MaterialLocation.OnCarrier(carrier.Id, "position-01");
        var address = new SlotAddress(lineId, "station-b", "slot-carrier");
        var slotLocation = MaterialLocation.InSlot(address);
        var slot = SlotOccupancy.Register(address, BaseTimeUtc);

        await using (var repository =
            new PostgreSqlProductionMaterialRepository(schema.ConnectionString))
        {
            Assert.True(await repository.TryAddAsync(unit));
            Assert.True(await repository.TryAddAsync(carrier));
            Assert.True(await repository.TryAddAsync(slot));

            var carrierEntry = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
                await repository.GetCarrierAsync(carrier.Id));
            Assert.True(carrierEntry.Aggregate.Arrive(
                stationA,
                BaseTimeUtc.AddSeconds(1)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                carriers: [new CarrierUpdate(carrierEntry.Aggregate, carrierEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        carrierMaterial,
                        null,
                        null,
                        stationA,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(1))
                ]));

            var unitEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await repository.GetProductionUnitAsync(unit.Id));
            Assert.True(unitEntry.Aggregate.Arrive(
                stationA,
                BaseTimeUtc.AddSeconds(2)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                productionUnits: [new ProductionUnitUpdate(unitEntry.Aggregate, unitEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        unitMaterial,
                        null,
                        null,
                        stationA,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(2))
                ]));

            unitEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await repository.GetProductionUnitAsync(unit.Id));
            Assert.True(unitEntry.Aggregate.Transfer(
                stationA,
                carrierPosition,
                BaseTimeUtc.AddSeconds(3)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                productionUnits: [new ProductionUnitUpdate(unitEntry.Aggregate, unitEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        unitMaterial,
                        null,
                        stationA,
                        carrierPosition,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(3))
                ]));

            carrierEntry = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
                await repository.GetCarrierAsync(carrier.Id));
            Assert.True(carrierEntry.Aggregate.Transfer(
                stationA,
                stationB,
                BaseTimeUtc.AddSeconds(4)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                carriers: [new CarrierUpdate(carrierEntry.Aggregate, carrierEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        carrierMaterial,
                        null,
                        stationA,
                        stationB,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(4))
                ]));

            var slotEntry = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
                await repository.GetSlotAsync(address));
            carrierEntry = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
                await repository.GetCarrierAsync(carrier.Id));
            Assert.True(slotEntry.Aggregate.Reserve(
                carrierMaterial,
                BaseTimeUtc.AddSeconds(5)).Succeeded);
            Assert.True(slotEntry.Aggregate.Load(
                carrierMaterial,
                BaseTimeUtc.AddSeconds(6)).Succeeded);
            Assert.True(carrierEntry.Aggregate.Transfer(
                stationB,
                slotLocation,
                BaseTimeUtc.AddSeconds(6)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                carriers: [new CarrierUpdate(carrierEntry.Aggregate, carrierEntry.Revision)],
                slots: [new SlotOccupancyUpdate(slotEntry.Aggregate, slotEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        address,
                        carrierMaterial,
                        null,
                        null,
                        null,
                        SlotOccupancyStatus.Available,
                        SlotOccupancyStatus.Reserved,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(5)),
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        address,
                        carrierMaterial,
                        null,
                        null,
                        null,
                        SlotOccupancyStatus.Reserved,
                        SlotOccupancyStatus.Occupied,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(6)),
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        carrierMaterial,
                        null,
                        stationB,
                        slotLocation,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(6))
                ]));

            unitEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await repository.GetProductionUnitAsync(unit.Id));
            Assert.True(unitEntry.Aggregate.Transfer(
                carrierPosition,
                stationB,
                BaseTimeUtc.AddSeconds(7)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                productionUnits: [new ProductionUnitUpdate(unitEntry.Aggregate, unitEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        unitMaterial,
                        null,
                        carrierPosition,
                        stationB,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(7))
                ]));

            slotEntry = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
                await repository.GetSlotAsync(address));
            carrierEntry = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
                await repository.GetCarrierAsync(carrier.Id));
            Assert.True(slotEntry.Aggregate.Unload(
                carrierMaterial,
                BaseTimeUtc.AddSeconds(8)).Succeeded);
            Assert.True(carrierEntry.Aggregate.Transfer(
                slotLocation,
                stationB,
                BaseTimeUtc.AddSeconds(8)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                carriers: [new CarrierUpdate(carrierEntry.Aggregate, carrierEntry.Revision)],
                slots: [new SlotOccupancyUpdate(slotEntry.Aggregate, slotEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        address,
                        carrierMaterial,
                        null,
                        null,
                        null,
                        SlotOccupancyStatus.Occupied,
                        SlotOccupancyStatus.Available,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(8)),
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        carrierMaterial,
                        null,
                        slotLocation,
                        stationB,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(8))
                ]));

            carrierEntry = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
                await repository.GetCarrierAsync(carrier.Id));
            Assert.True(carrierEntry.Aggregate.Transfer(
                stationB,
                stationC,
                BaseTimeUtc.AddSeconds(9)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                carriers: [new CarrierUpdate(carrierEntry.Aggregate, carrierEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        carrierMaterial,
                        null,
                        stationB,
                        stationC,
                        "integration-test",
                        BaseTimeUtc.AddSeconds(9))
                ]));
        }

        await using var restarted =
            new PostgreSqlProductionMaterialRepository(schema.ConnectionString);
        var reader = new ProductionUnitMaterialLifecycleReader(restarted);
        var result = await reader.GetAsync(unit.Id.Value);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(BaseTimeUtc.AddSeconds(7), result.Value.ObservedThroughUtc);
        Assert.Equal("StationQueue", result.Value.CurrentLocation?.Kind);
        Assert.Equal("station-b", result.Value.CurrentLocation?.StationSystemId);
        Assert.Null(result.Value.CurrentCarrierLocation);
        var carrierLocations = result.Value.MaterialLocationTransitions
            .Where(transition => transition.MaterialKind == "Carrier")
            .ToArray();
        Assert.Equal(2, carrierLocations.Length);
        Assert.Equal(
            [BaseTimeUtc.AddSeconds(4), BaseTimeUtc.AddSeconds(6)],
            carrierLocations.Select(transition => transition.OccurredAtUtc).ToArray());
        Assert.Equal(2, result.Value.SlotOccupancyTransitions.Count);
        Assert.All(
            result.Value.SlotOccupancyTransitions,
            transition => Assert.True(transition.OccurredAtUtc < BaseTimeUtc.AddSeconds(7)));
        Assert.DoesNotContain(
            result.Value.MaterialLocationTransitions,
            transition => transition.MaterialKind == "Carrier"
                && transition.OccurredAtUtc is var occurredAtUtc
                && (occurredAtUtc < BaseTimeUtc.AddSeconds(3)
                    || occurredAtUtc >= BaseTimeUtc.AddSeconds(7)));
    }

    [PostgresIntegrationFact]
    public async Task MultiAggregateCommitRollsBackEveryEarlierWriteOnRevisionConflict()
    {
        var unique = Guid.NewGuid().ToString("N");
        var unit = CreateUnit($"ATOMIC-{unique}");
        var address = new SlotAddress($"line-{unique}", "station-a", "slot-atomic");
        var slot = SlotOccupancy.Register(address, BaseTimeUtc);

        await using var repository =
            new PostgreSqlProductionMaterialRepository(fixture.ConnectionString);
        Assert.True(await repository.TryAddAsync(unit));
        Assert.True(await repository.TryAddAsync(slot));

        var unitEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await repository.GetProductionUnitAsync(unit.Id));
        var staleSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await repository.GetSlotAsync(address));
        var currentSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await repository.GetSlotAsync(address));
        Assert.True(currentSlot.Aggregate.Block("maintenance", BaseTimeUtc.AddSeconds(1)).Succeeded);
        await repository.CommitAsync(new ProductionMaterialCommit(
            slots: [new SlotOccupancyUpdate(currentSlot.Aggregate, currentSlot.Revision)],
            timeline:
            [
                ProductionMaterialTimelineEntry.SlotOccupancy(
                    Guid.NewGuid(), address, null, null,
                    null, null,
                    SlotOccupancyStatus.Available, SlotOccupancyStatus.Blocked,
                    "integration-test", BaseTimeUtc.AddSeconds(1))
            ]));

        Assert.True(unitEntry.Aggregate.Hold("quality review", BaseTimeUtc.AddSeconds(2)).Succeeded);
        Assert.True(staleSlot.Aggregate.Block(
            "stale maintenance",
            BaseTimeUtc.AddSeconds(2)).Succeeded);
        await Assert.ThrowsAsync<ProductionMaterialConcurrencyException>(async () =>
            await repository.CommitAsync(new ProductionMaterialCommit(
                productionUnits:
                [
                    new ProductionUnitUpdate(unitEntry.Aggregate, unitEntry.Revision)
                ],
                slots:
                [
                    new SlotOccupancyUpdate(staleSlot.Aggregate, staleSlot.Revision)
                ],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Disposition(
                        Guid.NewGuid(), unit.Id, null,
                        ProductDisposition.InProcess, ProductDisposition.Held,
                        "quality review", "integration-test", BaseTimeUtc.AddSeconds(2)),
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(), address, null, null,
                        null, null,
                        SlotOccupancyStatus.Available, SlotOccupancyStatus.Blocked,
                        "integration-test", BaseTimeUtc.AddSeconds(2))
                ])));

        var persistedUnit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await repository.GetProductionUnitAsync(unit.Id));
        var persistedSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await repository.GetSlotAsync(address));
        Assert.Equal(ProductDisposition.InProcess, persistedUnit.Aggregate.Disposition);
        Assert.Equal(0, persistedUnit.Revision);
        Assert.Equal(SlotOccupancyStatus.Blocked, persistedSlot.Aggregate.Status);
        Assert.Equal(1, persistedSlot.Revision);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentWritersAllowExactlyOneRevisionTransition()
    {
        var unique = Guid.NewGuid().ToString("N");
        var unit = CreateUnit($"CAS-{unique}");
        await using var repository =
            new PostgreSqlProductionMaterialRepository(fixture.ConnectionString);
        Assert.True(await repository.TryAddAsync(unit));

        var first = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await repository.GetProductionUnitAsync(unit.Id));
        var second = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await repository.GetProductionUnitAsync(unit.Id));
        Assert.True(first.Aggregate.Hold("first writer", BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(second.Aggregate.MarkNonconforming(
            "second writer",
            BaseTimeUtc.AddSeconds(1)).Succeeded);

        var results = await Task.WhenAll(
            TryCommitAsync(repository, first),
            TryCommitAsync(repository, second));

        Assert.Single(results, static succeeded => succeeded);
        Assert.Single(results, static succeeded => !succeeded);
        var persisted = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await repository.GetProductionUnitAsync(unit.Id));
        Assert.Equal(1, persisted.Revision);
        Assert.Contains(
            persisted.Aggregate.Disposition,
            new[] { ProductDisposition.Held, ProductDisposition.Nonconforming });
    }

    private static async Task<bool> TryCommitAsync(
        PostgreSqlProductionMaterialRepository repository,
        ProductionMaterialPersistenceEntry<ProductionUnit> entry)
    {
        try
        {
            await repository.CommitAsync(new ProductionMaterialCommit(
                productionUnits:
                [
                    new ProductionUnitUpdate(entry.Aggregate, entry.Revision)
                ],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Disposition(
                        Guid.NewGuid(),
                        entry.Aggregate.Id,
                        null,
                        ProductDisposition.InProcess,
                        entry.Aggregate.Disposition,
                        entry.Aggregate.DispositionReason,
                        "integration-test",
                        entry.Aggregate.LastDispositionTransitionAtUtc)
                ]));
            return true;
        }
        catch (ProductionMaterialConcurrencyException)
        {
            return false;
        }
    }

    private static ProductionUnit CreateUnit(
        string serialNumber,
        ProductionLotId? lotId = null)
    {
        return ProductionUnit.Register(
            ProductionUnitId.New(),
            "controller-board",
            "serial-number",
            serialNumber,
            lotId,
            "integration-test",
            BaseTimeUtc);
    }
}
