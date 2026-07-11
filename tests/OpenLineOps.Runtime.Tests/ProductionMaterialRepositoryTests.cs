using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionMaterialRepositoryTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryCommitUsesCasAndRemainsAtomicOnConflict()
    {
        var repository = new InMemoryProductionMaterialRepository();

        await AssertAtomicConflict(repository);
    }

    [Fact]
    public async Task SqliteCommitUsesCasAndRemainsAtomicOnConflict()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProductionMaterialRepository(database.ConnectionString);

        await AssertAtomicConflict(repository);
    }

    [Fact]
    public async Task SqliteRepositoryRoundTripsCompleteMaterialStateAfterRestart()
    {
        using var database = TemporarySqliteDatabase.Create();
        var lot = ProductionLot.Register(
            new ProductionLotId("lot-a"),
            "board-a",
            25,
            "operator-a",
            BaseTimeUtc);
        var unit = ProductionUnit.Register(
            ProductionUnitId.New(),
            "board-a",
            "serial-number",
            "SN-SQLITE-001",
            lot.Id,
            "operator-a",
            BaseTimeUtc);
        var carrier = Carrier.Register(
            new CarrierId("carrier-a"),
            "tray-24",
            24,
            "operator-a",
            BaseTimeUtc);
        var address = new SlotAddress("line-a", "station-a", "slot-01");
        var slot = SlotOccupancy.Register(address, BaseTimeUtc);
        var material = MaterialReference.ForProductionUnit(unit.Id);
        var station = MaterialLocation.AtStation(address.LineId, address.StationSystemId);
        var genealogy = new MaterialGenealogyLink(
            MaterialGenealogyLinkId.New(),
            ProductionUnitId.New(),
            unit.Id,
            "ComponentOf",
            "operation-a",
            "operator-a",
            BaseTimeUtc.AddSeconds(3));

        using (var repository = new SqliteProductionMaterialRepository(database.ConnectionString))
        {
            Assert.True(await repository.TryAddAsync(lot));
            Assert.True(await repository.TryAddAsync(unit));
            Assert.True(await repository.TryAddAsync(carrier));
            Assert.True(await repository.TryAddAsync(slot));
            var unitEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await repository.GetProductionUnitAsync(unit.Id));
            var slotEntry = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
                await repository.GetSlotAsync(address));
            Assert.True(unitEntry.Aggregate.Arrive(station, BaseTimeUtc.AddSeconds(1)).Succeeded);
            Assert.True(slotEntry.Aggregate.Reserve(material, BaseTimeUtc.AddSeconds(2)).Succeeded);
            await repository.CommitAsync(new ProductionMaterialCommit(
                productionUnits:
                [new ProductionUnitUpdate(unitEntry.Aggregate, unitEntry.Revision)],
                slots: [new SlotOccupancyUpdate(slotEntry.Aggregate, slotEntry.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Location(
                        Guid.NewGuid(),
                        material,
                        null,
                        null,
                        station,
                        "operator-a",
                        BaseTimeUtc.AddSeconds(1)),
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        address,
                        material,
                        null,
                        null,
                        null,
                        SlotOccupancyStatus.Available,
                        SlotOccupancyStatus.Reserved,
                        "operator-a",
                        BaseTimeUtc.AddSeconds(2))
                ]));
            Assert.True(await repository.TryAddAsync(genealogy));
        }

        using var restarted = new SqliteProductionMaterialRepository(database.ConnectionString);
        var restoredLot = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionLot>>(
            await restarted.GetProductionLotAsync(lot.Id));
        var restoredUnit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await restarted.GetProductionUnitAsync(unit.Id));
        var restoredCarrier = Assert.IsType<ProductionMaterialPersistenceEntry<Carrier>>(
            await restarted.GetCarrierAsync(carrier.Id));
        var listedCarrier = Assert.Single(
            await restarted.ListCarriersAsync(),
            entry => entry.Aggregate.Id == carrier.Id);
        var restoredSlot = Assert.IsType<ProductionMaterialPersistenceEntry<SlotOccupancy>>(
            await restarted.GetSlotAsync(address));
        var restoredLink = Assert.Single(await restarted.ListGenealogyLinksAsync());

        Assert.Equal(25, restoredLot.Aggregate.DeclaredQuantity);
        Assert.Equal(station, restoredUnit.Aggregate.Location);
        Assert.Equal(ProductDisposition.InProcess, restoredUnit.Aggregate.Disposition);
        Assert.Equal(24, restoredCarrier.Aggregate.Capacity);
        Assert.Equal(carrier.Id, listedCarrier.Aggregate.Id);
        Assert.Equal(SlotOccupancyStatus.Reserved, restoredSlot.Aggregate.Status);
        Assert.Equal(material, restoredSlot.Aggregate.Material);
        Assert.Equal(genealogy, restoredLink);
        Assert.All(
            new[] { restoredLot.Revision, restoredUnit.Revision, restoredCarrier.Revision, restoredSlot.Revision },
            revision => Assert.True(revision is 0 or 1));
        Assert.Equal(1, restoredUnit.Revision);
        Assert.Equal(1, restoredSlot.Revision);
    }

    [Fact]
    public async Task SqliteTimelineRebuildsCarrierSlotLocationAndGenealogyEvidenceAfterRestart()
    {
        using var database = TemporarySqliteDatabase.Create();
        var parentId = ProductionUnitId.New();
        var childId = ProductionUnitId.New();
        var carrierId = new CarrierId("carrier-timeline");
        var slot = new SlotAddress("line-a", "station-assembly", "slot-timeline");
        var station = MaterialLocation.AtStation(slot.LineId, slot.StationSystemId);
        var nextStation = MaterialLocation.AtStation("line-a", "station-next");
        var child = MaterialReference.ForProductionUnit(childId);
        var carrier = MaterialReference.ForCarrier(carrierId);
        var linkId = MaterialGenealogyLinkId.New();

        using (var materials = new SqliteProductionMaterialRepository(database.ConnectionString))
        using (var runs = new SqliteProductionRunRepository(database.ConnectionString))
        {
            var service = new ProductionMaterialService(materials, runs);
            Assert.True((await service.RegisterUnitAsync(new RegisterProductionUnitCommand(
                parentId,
                "board-a",
                "serial-number",
                "SN-PARENT",
                null,
                "operator-a",
                BaseTimeUtc))).Succeeded);
            Assert.True((await service.RegisterUnitAsync(new RegisterProductionUnitCommand(
                childId,
                "board-a",
                "serial-number",
                "SN-CHILD",
                null,
                "operator-a",
                BaseTimeUtc))).Succeeded);
            Assert.True((await service.RegisterCarrierAsync(new RegisterCarrierCommand(
                carrierId,
                "tray-4",
                4,
                "operator-a",
                BaseTimeUtc))).Succeeded);
            Assert.True((await service.RegisterSlotAsync(new RegisterSlotCommand(
                slot,
                "engineer-a",
                BaseTimeUtc))).Succeeded);
            Assert.True((await service.ArriveAsync(new ArriveMaterialCommand(
                Guid.NewGuid(),
                child,
                station,
                "scanner-a",
                BaseTimeUtc.AddSeconds(1)))).Succeeded);
            Assert.True((await service.ArriveAsync(new ArriveMaterialCommand(
                Guid.NewGuid(),
                carrier,
                station,
                "scanner-a",
                BaseTimeUtc.AddSeconds(2)))).Succeeded);
            Assert.True((await service.TransferAsync(new TransferMaterialCommand(
                child,
                station,
                MaterialLocation.OnCarrier(carrierId, "position-01"),
                "operator-a",
                BaseTimeUtc.AddSeconds(3)))).Succeeded);
            Assert.True((await service.LinkGenealogyAsync(new LinkMaterialGenealogyCommand(
                linkId,
                parentId,
                childId,
                "ComponentOf",
                "operation.assembly",
                "operator-a",
                BaseTimeUtc.AddSeconds(4)))).Succeeded);
            Assert.True((await service.ReserveSlotAsync(new ReserveSlotCommand(
                slot,
                carrier,
                "coordinator",
                BaseTimeUtc.AddSeconds(5)))).Succeeded);
            Assert.True((await service.LoadSlotAsync(new LoadSlotCommand(
                slot,
                carrier,
                "operator-a",
                BaseTimeUtc.AddSeconds(6)))).Succeeded);
            Assert.True((await service.StartSlotAsync(new StartSlotCommand(
                slot,
                carrier,
                "agent-a",
                BaseTimeUtc.AddSeconds(7)))).Succeeded);
            Assert.True((await service.CompleteSlotAsync(new CompleteSlotCommand(
                slot,
                carrier,
                "agent-a",
                BaseTimeUtc.AddSeconds(8)))).Succeeded);
            Assert.True((await service.UnloadSlotAsync(new UnloadSlotCommand(
                slot,
                carrier,
                nextStation,
                "operator-a",
                BaseTimeUtc.AddSeconds(9)))).Succeeded);
        }

        using var restarted = new SqliteProductionMaterialRepository(database.ConnectionString);
        var timeline = await restarted.ListTimelineAsync(new ProductionMaterialTimelineQuery(
            productionUnitId: childId,
            carrierId: carrierId));
        Assert.Equal(5, timeline.Count(entry =>
            entry.Kind == ProductionMaterialEvidenceKind.LocationTransition));
        Assert.Equal(5, timeline.Count(entry =>
            entry.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition));
        var genealogy = Assert.Single(timeline, entry =>
            entry.Kind == ProductionMaterialEvidenceKind.Genealogy);
        Assert.Equal(linkId, genealogy.Genealogy?.Id);
        Assert.Contains(timeline, entry =>
            entry.DestinationLocation == MaterialLocation.OnCarrier(carrierId, "position-01"));
        Assert.Contains(timeline, entry => entry.DestinationLocation == nextStation);
        Assert.Equal(
            [
                SlotOccupancyStatus.Reserved,
                SlotOccupancyStatus.Occupied,
                SlotOccupancyStatus.Running,
                SlotOccupancyStatus.Occupied,
                SlotOccupancyStatus.Available
            ],
            timeline
                .Where(entry => entry.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition)
                .Select(entry => entry.CurrentSlotStatus!.Value)
                .ToArray());
    }

    [Fact]
    public async Task RepositoriesRejectDuplicateCanonicalProductIdentity()
    {
        var repository = new InMemoryProductionMaterialRepository();
        var first = CreateUnit("SN-DUPLICATE");
        var second = CreateUnit("SN-DUPLICATE");

        Assert.True(await repository.TryAddAsync(first));
        Assert.False(await repository.TryAddAsync(second));
        Assert.Single(await repository.ListProductionUnitsAsync());
    }

    [Fact]
    public async Task SqliteRepositoryRequiresExactCanonicalOccupancyStatusToken()
    {
        using var database = TemporarySqliteDatabase.Create();
        var address = new SlotAddress("line-a", "station-a", "slot-a");
        using (var repository = new SqliteProductionMaterialRepository(database.ConnectionString))
        {
            Assert.True(await repository.TryAddAsync(SlotOccupancy.Register(address, BaseTimeUtc)));
        }

        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT document_json
                FROM slot_occupancies
                WHERE line_id = $line_id
                  AND station_system_id = $station_system_id
                  AND slot_id = $slot_id;
                """;
            select.Parameters.AddWithValue("$line_id", address.LineId);
            select.Parameters.AddWithValue("$station_system_id", address.StationSystemId);
            select.Parameters.AddWithValue("$slot_id", address.SlotId);
            var document = JsonNode.Parse((string)(await select.ExecuteScalarAsync())!)!.AsObject();
            document["status"] = "available";

            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE slot_occupancies
                SET document_json = $document
                WHERE line_id = $line_id
                  AND station_system_id = $station_system_id
                  AND slot_id = $slot_id;
                """;
            update.Parameters.AddWithValue("$document", document.ToJsonString());
            update.Parameters.AddWithValue("$line_id", address.LineId);
            update.Parameters.AddWithValue("$station_system_id", address.StationSystemId);
            update.Parameters.AddWithValue("$slot_id", address.SlotId);
            await update.ExecuteNonQueryAsync();
        }

        using var restarted = new SqliteProductionMaterialRepository(database.ConnectionString);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await restarted.GetSlotAsync(address));
        Assert.Contains("case-sensitive", exception.Message, StringComparison.Ordinal);
        Assert.Contains("available", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=file:materials?mode=memory&cache=shared")]
    public void SqliteRepositoryRejectsMemoryDatabase(string connectionString)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new SqliteProductionMaterialRepository(connectionString));

        Assert.Contains("file-backed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlRepositoryDeclaresTheOnlyStrictMaterialSchema()
    {
        var schema = PostgreSqlProductionMaterialRepository.SchemaSql;

        Assert.Contains("CREATE TABLE IF NOT EXISTS olo_production_units", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS olo_production_lots", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS olo_carriers", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS olo_slot_occupancies", schema, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE IF NOT EXISTS olo_material_genealogy_links",
            schema,
            StringComparison.Ordinal);
        Assert.Contains("document_json jsonb NOT NULL", schema, StringComparison.Ordinal);
        Assert.Contains("revision bigint NOT NULL", schema, StringComparison.Ordinal);
        Assert.Contains("uq_olo_production_units_identity", schema, StringComparison.Ordinal);
        Assert.Contains("uq_olo_slot_occupancies_material", schema, StringComparison.Ordinal);
        Assert.Contains("ck_olo_slot_occupancies_binding", schema, StringComparison.Ordinal);
        Assert.Contains(
            "REFERENCES olo_production_units(production_unit_id) ON DELETE RESTRICT",
            schema,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Host=localhost;Database=openlineops;Username=openlineops")]
    [InlineData("Host=localhost;Database=openlineops;Username=openlineops ")]
    [InlineData("Host=localhost;Database=openlineops")]
    public void PostgreSqlRepositoryRejectsIncompleteOrNonCanonicalConnectionStrings(
        string connectionString)
    {
        Assert.Throws<ArgumentException>(
            () => new PostgreSqlProductionMaterialRepository(connectionString));
    }

    [Fact]
    public async Task PostgreSqlRepositoryRejectsUseAfterContainerDisposalWithoutOpeningADatabase()
    {
        var repository = new PostgreSqlProductionMaterialRepository(
            "Host=localhost;Database=openlineops;Username=openlineops;Password=not-used");

        repository.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await repository.ListProductionUnitsAsync());
    }

    private static async Task AssertAtomicConflict(IProductionMaterialRepository repository)
    {
        var unit = CreateUnit("SN-ATOMIC");
        var address = new SlotAddress("line-a", "station-a", "slot-atomic");
        var slot = SlotOccupancy.Register(address, BaseTimeUtc);
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
                    Guid.NewGuid(),
                    address,
                    null,
                    null,
                    null,
                    null,
                    SlotOccupancyStatus.Available,
                    SlotOccupancyStatus.Blocked,
                    "operator-a",
                    BaseTimeUtc.AddSeconds(1))
            ]));

        Assert.True(unitEntry.Aggregate.Hold("quality review", BaseTimeUtc.AddSeconds(2)).Succeeded);
        Assert.True(staleSlot.Aggregate.Block("stale maintenance", BaseTimeUtc.AddSeconds(2)).Succeeded);
        await Assert.ThrowsAsync<ProductionMaterialConcurrencyException>(async () =>
            await repository.CommitAsync(new ProductionMaterialCommit(
                productionUnits: [new ProductionUnitUpdate(unitEntry.Aggregate, unitEntry.Revision)],
                slots: [new SlotOccupancyUpdate(staleSlot.Aggregate, staleSlot.Revision)],
                timeline:
                [
                    ProductionMaterialTimelineEntry.Disposition(
                        Guid.NewGuid(),
                        unit.Id,
                        null,
                        ProductDisposition.InProcess,
                        ProductDisposition.Held,
                        "quality review",
                        "operator-a",
                        BaseTimeUtc.AddSeconds(2)),
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        address,
                        null,
                        null,
                        null,
                        null,
                        SlotOccupancyStatus.Available,
                        SlotOccupancyStatus.Blocked,
                        "operator-a",
                        BaseTimeUtc.AddSeconds(2))
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

    private static ProductionUnit CreateUnit(string serialNumber)
    {
        return ProductionUnit.Register(
            ProductionUnitId.New(),
            "board-a",
            "serial-number",
            serialNumber,
            null,
            "operator-a",
            BaseTimeUtc);
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string connectionString)
        {
            Directory = directory;
            ConnectionString = connectionString;
        }

        public string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "OpenLineOps",
                Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "production-materials.sqlite");
            return new TemporarySqliteDatabase(
                directory,
                $"Data Source={databasePath};Pooling=False");
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
