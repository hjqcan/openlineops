using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteProductionMaterialRepository : IProductionMaterialRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteProductionMaterialRepository(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<bool> TryAddAsync(
        ProductionUnit productionUnit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionUnit);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var document = JsonSerializer.Serialize(
            ProductionMaterialSnapshotMapper.ToSnapshot(productionUnit),
            JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO production_units (
                production_unit_id,
                product_model_id,
                identity_key,
                identity_value,
                document_json,
                revision,
                disposition,
                updated_at_utc)
            VALUES (
                $production_unit_id,
                $product_model_id,
                $identity_key,
                $identity_value,
                $document_json,
                0,
                $disposition,
                $updated_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue(
            "$production_unit_id",
            productionUnit.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$product_model_id", productionUnit.ProductModelId);
        command.Parameters.AddWithValue("$identity_key", productionUnit.IdentityKey);
        command.Parameters.AddWithValue("$identity_value", productionUnit.IdentityValue);
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue("$disposition", productionUnit.Disposition.ToString());
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(productionUnit.LastTransitionAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> TryAddAsync(
        ProductionLot productionLot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionLot);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var document = JsonSerializer.Serialize(
            ProductionMaterialSnapshotMapper.ToSnapshot(productionLot),
            JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO production_lots (
                production_lot_id,
                product_model_id,
                document_json,
                revision,
                registered_at_utc)
            VALUES (
                $production_lot_id,
                $product_model_id,
                $document_json,
                0,
                $registered_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$production_lot_id", productionLot.Id.Value);
        command.Parameters.AddWithValue("$product_model_id", productionLot.ProductModelId);
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue("$registered_at_utc", FormatTimestamp(productionLot.RegisteredAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> TryAddAsync(
        Carrier carrier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var document = JsonSerializer.Serialize(
            ProductionMaterialSnapshotMapper.ToSnapshot(carrier),
            JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO carriers (
                carrier_id,
                carrier_type_id,
                document_json,
                revision,
                updated_at_utc)
            VALUES (
                $carrier_id,
                $carrier_type_id,
                $document_json,
                0,
                $updated_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$carrier_id", carrier.Id.Value);
        command.Parameters.AddWithValue("$carrier_type_id", carrier.CarrierTypeId);
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(carrier.LastTransitionAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> TryAddAsync(
        SlotOccupancy slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var document = JsonSerializer.Serialize(
            ProductionMaterialSnapshotMapper.ToSnapshot(slot),
            JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO slot_occupancies (
                line_id,
                station_system_id,
                slot_id,
                document_json,
                revision,
                status,
                updated_at_utc)
            VALUES (
                $line_id,
                $station_system_id,
                $slot_id,
                $document_json,
                0,
                $status,
                $updated_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        AddSlotIdentity(command, slot.Address);
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue("$status", slot.Status.ToString());
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(slot.LastTransitionAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> TryAddAsync(
        MaterialGenealogyLink link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var document = JsonSerializer.Serialize(
            ProductionMaterialSnapshotMapper.ToSnapshot(link),
            JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO material_genealogy_links (
                link_id,
                parent_unit_id,
                child_unit_id,
                relationship,
                document_json,
                linked_at_utc)
            VALUES (
                $link_id,
                $parent_unit_id,
                $child_unit_id,
                $relationship,
                $document_json,
                $linked_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$link_id", link.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$parent_unit_id", link.ParentUnitId.Value.ToString("D"));
        command.Parameters.AddWithValue("$child_unit_id", link.ChildUnitId.Value.ToString("D"));
        command.Parameters.AddWithValue("$relationship", link.Relationship);
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue("$linked_at_utc", FormatTimestamp(link.LinkedAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<ProductionMaterialPersistenceEntry<ProductionUnit>?>
        GetProductionUnitAsync(
            ProductionUnitId productionUnitId,
            CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM production_units
            WHERE production_unit_id = $production_unit_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(
            "$production_unit_id",
            productionUnitId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProductionMaterialPersistenceEntry<ProductionUnit>(
                DeserializeProductionUnit(reader.GetString(0)),
                reader.GetInt64(1))
            : null;
    }

    public async ValueTask<ProductionMaterialPersistenceEntry<ProductionLot>?> GetProductionLotAsync(
        ProductionLotId productionLotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionLotId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM production_lots
            WHERE production_lot_id = $production_lot_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$production_lot_id", productionLotId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProductionMaterialPersistenceEntry<ProductionLot>(
                DeserializeProductionLot(reader.GetString(0)),
                reader.GetInt64(1))
            : null;
    }

    public async ValueTask<ProductionMaterialPersistenceEntry<Carrier>?> GetCarrierAsync(
        CarrierId carrierId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrierId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM carriers
            WHERE carrier_id = $carrier_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$carrier_id", carrierId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProductionMaterialPersistenceEntry<Carrier>(
                DeserializeCarrier(reader.GetString(0)),
                reader.GetInt64(1))
            : null;
    }

    public async ValueTask<ProductionMaterialPersistenceEntry<SlotOccupancy>?> GetSlotAsync(
        SlotAddress slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM slot_occupancies
            WHERE line_id = $line_id
              AND station_system_id = $station_system_id
              AND slot_id = $slot_id
            LIMIT 1;
            """;
        AddSlotIdentity(command, slot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProductionMaterialPersistenceEntry<SlotOccupancy>(
                DeserializeSlot(reader.GetString(0)),
                reader.GetInt64(1))
            : null;
    }

    public async ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<ProductionUnit>>>
        ListProductionUnitsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM production_units
            ORDER BY updated_at_utc, production_unit_id;
            """;
        var units = new List<ProductionMaterialPersistenceEntry<ProductionUnit>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            units.Add(new ProductionMaterialPersistenceEntry<ProductionUnit>(
                DeserializeProductionUnit(reader.GetString(0)),
                reader.GetInt64(1)));
        }

        return units;
    }

    public async ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<SlotOccupancy>>>
        ListSlotsAsync(
            string? lineId = null,
            string? stationSystemId = null,
            CancellationToken cancellationToken = default)
    {
        ValidateFilter(lineId, nameof(lineId));
        ValidateFilter(stationSystemId, nameof(stationSystemId));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var predicates = new List<string>();
        if (lineId is not null)
        {
            predicates.Add("line_id = $line_id");
            command.Parameters.AddWithValue("$line_id", lineId);
        }

        if (stationSystemId is not null)
        {
            predicates.Add("station_system_id = $station_system_id");
            command.Parameters.AddWithValue("$station_system_id", stationSystemId);
        }

        command.CommandText = "SELECT document_json, revision FROM slot_occupancies"
            + (predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates))
            + " ORDER BY line_id, station_system_id, slot_id;";
        var slots = new List<ProductionMaterialPersistenceEntry<SlotOccupancy>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            slots.Add(new ProductionMaterialPersistenceEntry<SlotOccupancy>(
                DeserializeSlot(reader.GetString(0)),
                reader.GetInt64(1)));
        }

        return slots;
    }

    public async ValueTask<IReadOnlyCollection<MaterialGenealogyLink>> ListGenealogyLinksAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM material_genealogy_links
            ORDER BY linked_at_utc, link_id;
            """;
        var links = new List<MaterialGenealogyLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            links.Add(DeserializeGenealogyLink(reader.GetString(0)));
        }

        return links;
    }

    public async ValueTask CommitAsync(
        ProductionMaterialCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var update in commit.ProductionUnits)
        {
            await UpdateProductionUnitAsync(connection, transaction, update, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var update in commit.Carriers)
        {
            await UpdateCarrierAsync(connection, transaction, update, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var update in commit.Slots)
        {
            await UpdateSlotAsync(connection, transaction, update, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateProductionUnitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionUnitUpdate update,
        CancellationToken cancellationToken)
    {
        var document = JsonSerializer.Serialize(
            ProductionMaterialSnapshotMapper.ToSnapshot(update.Aggregate),
            JsonOptions);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE production_units
            SET document_json = $document_json,
                revision = $next_revision,
                disposition = $disposition,
                updated_at_utc = $updated_at_utc
            WHERE production_unit_id = $production_unit_id
              AND revision = $expected_revision;
            """;
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue("$next_revision", checked(update.ExpectedRevision + 1));
        command.Parameters.AddWithValue("$disposition", update.Aggregate.Disposition.ToString());
        command.Parameters.AddWithValue(
            "$updated_at_utc",
            FormatTimestamp(update.Aggregate.LastTransitionAtUtc));
        command.Parameters.AddWithValue(
            "$production_unit_id",
            update.Aggregate.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$expected_revision", update.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await ThrowConflictAsync(
                    connection,
                    transaction,
                    "production_units",
                    "production_unit_id",
                    update.Aggregate.Id.Value.ToString("D"),
                    "Production Unit",
                    update.Aggregate.Id.ToString(),
                    update.ExpectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask UpdateCarrierAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CarrierUpdate update,
        CancellationToken cancellationToken)
    {
        var document = JsonSerializer.Serialize(
            ProductionMaterialSnapshotMapper.ToSnapshot(update.Aggregate),
            JsonOptions);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE carriers
            SET document_json = $document_json,
                revision = $next_revision,
                updated_at_utc = $updated_at_utc
            WHERE carrier_id = $carrier_id
              AND revision = $expected_revision;
            """;
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue("$next_revision", checked(update.ExpectedRevision + 1));
        command.Parameters.AddWithValue(
            "$updated_at_utc",
            FormatTimestamp(update.Aggregate.LastTransitionAtUtc));
        command.Parameters.AddWithValue("$carrier_id", update.Aggregate.Id.Value);
        command.Parameters.AddWithValue("$expected_revision", update.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await ThrowConflictAsync(
                    connection,
                    transaction,
                    "carriers",
                    "carrier_id",
                    update.Aggregate.Id.Value,
                    "Carrier",
                    update.Aggregate.Id.Value,
                    update.ExpectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask UpdateSlotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SlotOccupancyUpdate update,
        CancellationToken cancellationToken)
    {
        var document = JsonSerializer.Serialize(
            ProductionMaterialSnapshotMapper.ToSnapshot(update.Aggregate),
            JsonOptions);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE slot_occupancies
            SET document_json = $document_json,
                revision = $next_revision,
                status = $status,
                updated_at_utc = $updated_at_utc
            WHERE line_id = $line_id
              AND station_system_id = $station_system_id
              AND slot_id = $slot_id
              AND revision = $expected_revision;
            """;
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue("$next_revision", checked(update.ExpectedRevision + 1));
        command.Parameters.AddWithValue("$status", update.Aggregate.Status.ToString());
        command.Parameters.AddWithValue(
            "$updated_at_utc",
            FormatTimestamp(update.Aggregate.LastTransitionAtUtc));
        AddSlotIdentity(command, update.Aggregate.Address);
        command.Parameters.AddWithValue("$expected_revision", update.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await ThrowSlotConflictAsync(
                    connection,
                    transaction,
                    update.Aggregate.Address,
                    update.ExpectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            EnsureDatabaseDirectory();
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS production_units (
                    production_unit_id TEXT NOT NULL PRIMARY KEY,
                    product_model_id TEXT NOT NULL,
                    identity_key TEXT NOT NULL,
                    identity_value TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    disposition TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    UNIQUE(product_model_id, identity_key, identity_value)
                );

                CREATE INDEX IF NOT EXISTS ix_production_units_disposition
                    ON production_units(disposition, updated_at_utc);

                CREATE TABLE IF NOT EXISTS production_lots (
                    production_lot_id TEXT NOT NULL PRIMARY KEY,
                    product_model_id TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    registered_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS carriers (
                    carrier_id TEXT NOT NULL PRIMARY KEY,
                    carrier_type_id TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS slot_occupancies (
                    line_id TEXT NOT NULL,
                    station_system_id TEXT NOT NULL,
                    slot_id TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY(line_id, station_system_id, slot_id)
                );

                CREATE INDEX IF NOT EXISTS ix_slot_occupancies_state
                    ON slot_occupancies(line_id, station_system_id, status, slot_id);

                CREATE TABLE IF NOT EXISTS material_genealogy_links (
                    link_id TEXT NOT NULL PRIMARY KEY,
                    parent_unit_id TEXT NOT NULL,
                    child_unit_id TEXT NOT NULL,
                    relationship TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    linked_at_utc TEXT NOT NULL,
                    UNIQUE(parent_unit_id, child_unit_id, relationship)
                );

                CREATE INDEX IF NOT EXISTS ix_material_genealogy_parent
                    ON material_genealogy_links(parent_unit_id, linked_at_utc);

                CREATE INDEX IF NOT EXISTS ix_material_genealogy_child
                    ON material_genealogy_links(child_unit_id, linked_at_utc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private static async ValueTask ThrowConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string keyColumn,
        string keyValue,
        string resourceKind,
        string resourceId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT revision FROM {tableName} WHERE {keyColumn} = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", keyValue);
        var storedRevision = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (storedRevision is null)
        {
            throw new InvalidOperationException(
                $"{resourceKind} {resourceId} must be added before it can be updated.");
        }

        throw new ProductionMaterialConcurrencyException(
            resourceKind,
            resourceId,
            expectedRevision);
    }

    private static async ValueTask ThrowSlotConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SlotAddress address,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM slot_occupancies
            WHERE line_id = $line_id
              AND station_system_id = $station_system_id
              AND slot_id = $slot_id
            LIMIT 1;
            """;
        AddSlotIdentity(command, address);
        var storedRevision = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (storedRevision is null)
        {
            throw new InvalidOperationException(
                $"Slot {address} must be added before it can be updated.");
        }

        throw new ProductionMaterialConcurrencyException(
            "Slot",
            address.ToString(),
            expectedRevision);
    }

    private static void AddSlotIdentity(SqliteCommand command, SlotAddress slot)
    {
        command.Parameters.AddWithValue("$line_id", slot.LineId);
        command.Parameters.AddWithValue("$station_system_id", slot.StationSystemId);
        command.Parameters.AddWithValue("$slot_id", slot.SlotId);
    }

    private static ProductionUnit DeserializeProductionUnit(string document)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedProductionUnit>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Production Unit document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static ProductionLot DeserializeProductionLot(string document)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedProductionLot>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Production Lot document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static Carrier DeserializeCarrier(string document)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedCarrier>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Carrier document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static SlotOccupancy DeserializeSlot(string document)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedSlotOccupancy>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Slot occupancy document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static MaterialGenealogyLink DeserializeGenealogyLink(string document)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedMaterialGenealogyLink>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Material genealogy document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static string RequireFileBackedConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        }

        var normalized = connectionString.Trim();
        var builder = new SqliteConnectionStringBuilder(normalized);
        if (builder.Mode == SqliteOpenMode.Memory
            || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Production Material SQLite persistence requires a file-backed database; "
                + "use the InMemory provider for transient execution.",
                nameof(connectionString));
        }

        return normalized;
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static void ValidateFilter(string? value, string parameterName)
    {
        if (value is not null
            && (string.IsNullOrWhiteSpace(value)
                || char.IsWhiteSpace(value[0])
                || char.IsWhiteSpace(value[^1])))
        {
            throw new ArgumentException("Filter must be canonical text when supplied.", parameterName);
        }
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
