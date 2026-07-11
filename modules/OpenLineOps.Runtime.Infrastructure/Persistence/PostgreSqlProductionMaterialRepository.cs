using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class PostgreSqlProductionMaterialRepository :
    IProductionMaterialRepository,
    IDisposable,
    IAsyncDisposable
{
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS olo_production_lots (
            production_lot_id text PRIMARY KEY,
            product_model_id text NOT NULL,
            declared_quantity integer NULL,
            document_json jsonb NOT NULL,
            revision bigint NOT NULL,
            registered_at_utc timestamptz NOT NULL,
            CONSTRAINT ck_olo_production_lots_id
                CHECK (production_lot_id <> '' AND production_lot_id = btrim(production_lot_id)),
            CONSTRAINT ck_olo_production_lots_model
                CHECK (product_model_id <> '' AND product_model_id = btrim(product_model_id)),
            CONSTRAINT ck_olo_production_lots_quantity
                CHECK (declared_quantity IS NULL OR declared_quantity > 0),
            CONSTRAINT ck_olo_production_lots_revision CHECK (revision >= 0)
        );

        CREATE TABLE IF NOT EXISTS olo_production_units (
            production_unit_id uuid PRIMARY KEY,
            product_model_id text NOT NULL,
            identity_key text NOT NULL,
            identity_value text NOT NULL,
            production_lot_id text NULL
                REFERENCES olo_production_lots(production_lot_id) ON DELETE RESTRICT,
            document_json jsonb NOT NULL,
            revision bigint NOT NULL,
            disposition text NOT NULL,
            updated_at_utc timestamptz NOT NULL,
            CONSTRAINT uq_olo_production_units_identity
                UNIQUE(product_model_id, identity_key, identity_value),
            CONSTRAINT ck_olo_production_units_identity_key
                CHECK (identity_key <> '' AND identity_key = btrim(identity_key)),
            CONSTRAINT ck_olo_production_units_identity_value
                CHECK (identity_value <> '' AND identity_value = btrim(identity_value)),
            CONSTRAINT ck_olo_production_units_revision CHECK (revision >= 0),
            CONSTRAINT ck_olo_production_units_disposition CHECK (
                disposition IN ('InProcess', 'Completed', 'Nonconforming', 'Held', 'Scrapped'))
        );
        CREATE INDEX IF NOT EXISTS ix_olo_production_units_disposition
            ON olo_production_units(disposition, updated_at_utc, production_unit_id);
        CREATE INDEX IF NOT EXISTS ix_olo_production_units_lot
            ON olo_production_units(production_lot_id, production_unit_id)
            WHERE production_lot_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS olo_carriers (
            carrier_id text PRIMARY KEY,
            carrier_type_id text NOT NULL,
            capacity integer NOT NULL,
            document_json jsonb NOT NULL,
            revision bigint NOT NULL,
            updated_at_utc timestamptz NOT NULL,
            CONSTRAINT ck_olo_carriers_id
                CHECK (carrier_id <> '' AND carrier_id = btrim(carrier_id)),
            CONSTRAINT ck_olo_carriers_type
                CHECK (carrier_type_id <> '' AND carrier_type_id = btrim(carrier_type_id)),
            CONSTRAINT ck_olo_carriers_capacity CHECK (capacity > 0),
            CONSTRAINT ck_olo_carriers_revision CHECK (revision >= 0)
        );

        CREATE TABLE IF NOT EXISTS olo_slot_occupancies (
            line_id text NOT NULL,
            station_system_id text NOT NULL,
            slot_id text NOT NULL,
            document_json jsonb NOT NULL,
            revision bigint NOT NULL,
            status text NOT NULL,
            material_kind text NULL,
            material_id text NULL,
            updated_at_utc timestamptz NOT NULL,
            PRIMARY KEY(line_id, station_system_id, slot_id),
            CONSTRAINT ck_olo_slot_occupancies_revision CHECK (revision >= 0),
            CONSTRAINT ck_olo_slot_occupancies_status CHECK (
                status IN ('Available', 'Reserved', 'Occupied', 'Running', 'Blocked', 'Offline')),
            CONSTRAINT ck_olo_slot_occupancies_material_kind CHECK (
                material_kind IS NULL OR material_kind IN ('ProductionUnit', 'Carrier')),
            CONSTRAINT ck_olo_slot_occupancies_binding CHECK (
                (status IN ('Available', 'Blocked', 'Offline')
                    AND material_kind IS NULL AND material_id IS NULL)
                OR
                (status IN ('Reserved', 'Occupied', 'Running')
                    AND material_kind IS NOT NULL
                    AND material_id IS NOT NULL
                    AND material_id <> ''
                    AND material_id = btrim(material_id)))
        );
        CREATE INDEX IF NOT EXISTS ix_olo_slot_occupancies_state
            ON olo_slot_occupancies(line_id, station_system_id, status, slot_id);
        CREATE UNIQUE INDEX IF NOT EXISTS uq_olo_slot_occupancies_material
            ON olo_slot_occupancies(material_kind, material_id)
            WHERE material_kind IS NOT NULL;

        CREATE TABLE IF NOT EXISTS olo_material_genealogy_links (
            link_id uuid PRIMARY KEY,
            parent_unit_id uuid NOT NULL
                REFERENCES olo_production_units(production_unit_id) ON DELETE RESTRICT,
            child_unit_id uuid NOT NULL
                REFERENCES olo_production_units(production_unit_id) ON DELETE RESTRICT,
            relationship text NOT NULL,
            operation_id text NOT NULL,
            linked_by text NOT NULL,
            document_json jsonb NOT NULL,
            linked_at_utc timestamptz NOT NULL,
            CONSTRAINT uq_olo_material_genealogy_relationship
                UNIQUE(parent_unit_id, child_unit_id, relationship),
            CONSTRAINT ck_olo_material_genealogy_distinct_units
                CHECK (parent_unit_id <> child_unit_id),
            CONSTRAINT ck_olo_material_genealogy_relationship
                CHECK (relationship <> '' AND relationship = btrim(relationship))
        );
        CREATE INDEX IF NOT EXISTS ix_olo_material_genealogy_parent
            ON olo_material_genealogy_links(parent_unit_id, linked_at_utc, link_id);
        CREATE INDEX IF NOT EXISTS ix_olo_material_genealogy_child
            ON olo_material_genealogy_links(child_unit_id, linked_at_utc, link_id);

        CREATE TABLE IF NOT EXISTS olo_production_material_timeline (
            evidence_id uuid PRIMARY KEY,
            kind text NOT NULL,
            production_run_id uuid NULL,
            production_unit_id uuid NULL,
            carrier_id text NULL,
            genealogy_parent_unit_id uuid NULL,
            genealogy_child_unit_id uuid NULL,
            document_json jsonb NOT NULL,
            occurred_at_utc timestamptz NOT NULL,
            CONSTRAINT ck_olo_production_material_timeline_kind CHECK (
                kind IN ('LocationTransition', 'SlotOccupancyTransition',
                    'DispositionTransition', 'Genealogy'))
        );
        CREATE INDEX IF NOT EXISTS ix_olo_production_material_timeline_unit
            ON olo_production_material_timeline(production_unit_id, occurred_at_utc, evidence_id);
        CREATE INDEX IF NOT EXISTS ix_olo_production_material_timeline_run
            ON olo_production_material_timeline(production_run_id, occurred_at_utc, evidence_id);
        CREATE INDEX IF NOT EXISTS ix_olo_production_material_timeline_carrier
            ON olo_production_material_timeline(carrier_id, occurred_at_utc, evidence_id);
        CREATE INDEX IF NOT EXISTS ix_olo_production_material_timeline_genealogy_parent
            ON olo_production_material_timeline(genealogy_parent_unit_id, occurred_at_utc, evidence_id);
        CREATE INDEX IF NOT EXISTS ix_olo_production_material_timeline_genealogy_child
            ON olo_production_material_timeline(genealogy_child_unit_id, occurred_at_utc, evidence_id);
        """;

    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();

    private static readonly IReadOnlyDictionary<string, ExpectedColumn[]> ExpectedSchema =
        new Dictionary<string, ExpectedColumn[]>(StringComparer.Ordinal)
        {
            ["olo_production_lots"] =
            [
                new("production_lot_id", "text", false),
                new("product_model_id", "text", false),
                new("declared_quantity", "int4", true),
                new("document_json", "jsonb", false),
                new("revision", "int8", false),
                new("registered_at_utc", "timestamptz", false)
            ],
            ["olo_production_units"] =
            [
                new("production_unit_id", "uuid", false),
                new("product_model_id", "text", false),
                new("identity_key", "text", false),
                new("identity_value", "text", false),
                new("production_lot_id", "text", true),
                new("document_json", "jsonb", false),
                new("revision", "int8", false),
                new("disposition", "text", false),
                new("updated_at_utc", "timestamptz", false)
            ],
            ["olo_carriers"] =
            [
                new("carrier_id", "text", false),
                new("carrier_type_id", "text", false),
                new("capacity", "int4", false),
                new("document_json", "jsonb", false),
                new("revision", "int8", false),
                new("updated_at_utc", "timestamptz", false)
            ],
            ["olo_slot_occupancies"] =
            [
                new("line_id", "text", false),
                new("station_system_id", "text", false),
                new("slot_id", "text", false),
                new("document_json", "jsonb", false),
                new("revision", "int8", false),
                new("status", "text", false),
                new("material_kind", "text", true),
                new("material_id", "text", true),
                new("updated_at_utc", "timestamptz", false)
            ],
            ["olo_material_genealogy_links"] =
            [
                new("link_id", "uuid", false),
                new("parent_unit_id", "uuid", false),
                new("child_unit_id", "uuid", false),
                new("relationship", "text", false),
                new("operation_id", "text", false),
                new("linked_by", "text", false),
                new("document_json", "jsonb", false),
                new("linked_at_utc", "timestamptz", false)
            ],
            ["olo_production_material_timeline"] =
            [
                new("evidence_id", "uuid", false),
                new("kind", "text", false),
                new("production_run_id", "uuid", true),
                new("production_unit_id", "uuid", true),
                new("carrier_id", "text", true),
                new("genealogy_parent_unit_id", "uuid", true),
                new("genealogy_child_unit_id", "uuid", true),
                new("document_json", "jsonb", false),
                new("occurred_at_utc", "timestamptz", false)
            ]
        };

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;
    private int _disposed;

    public PostgreSqlProductionMaterialRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)
            || char.IsWhiteSpace(connectionString[0])
            || char.IsWhiteSpace(connectionString[^1]))
        {
            throw new ArgumentException(
                "PostgreSQL Production Material connection string must be canonical.",
                nameof(connectionString));
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Host)
            || string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new ArgumentException(
                "PostgreSQL Production Material persistence requires Host, Database, and Username.",
                nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async ValueTask<bool> TryAddAsync(
        ProductionUnit productionUnit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionUnit);
        ProductionMaterialRegistrationGuard.RequireInitial(productionUnit);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO olo_production_units (
                production_unit_id, product_model_id, identity_key, identity_value,
                production_lot_id, document_json, revision, disposition, updated_at_utc)
            VALUES (
                @production_unit_id, @product_model_id, @identity_key, @identity_value,
                @production_lot_id, @document_json::jsonb, 0, @disposition, @updated_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("production_unit_id", productionUnit.Id.Value);
        command.Parameters.AddWithValue("product_model_id", productionUnit.ProductModelId);
        command.Parameters.AddWithValue("identity_key", productionUnit.IdentityKey);
        command.Parameters.AddWithValue("identity_value", productionUnit.IdentityValue);
        command.Parameters.Add("production_lot_id", NpgsqlDbType.Text).Value =
            (object?)productionUnit.LotId?.Value ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(productionUnit)));
        command.Parameters.AddWithValue("disposition", productionUnit.Disposition.ToString());
        command.Parameters.AddWithValue("updated_at_utc", productionUnit.LastTransitionAtUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> TryAddAsync(
        ProductionLot productionLot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionLot);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO olo_production_lots (
                production_lot_id, product_model_id, declared_quantity,
                document_json, revision, registered_at_utc)
            VALUES (
                @production_lot_id, @product_model_id, @declared_quantity,
                @document_json::jsonb, 0, @registered_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("production_lot_id", productionLot.Id.Value);
        command.Parameters.AddWithValue("product_model_id", productionLot.ProductModelId);
        command.Parameters.Add("declared_quantity", NpgsqlDbType.Integer).Value =
            (object?)productionLot.DeclaredQuantity ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(productionLot)));
        command.Parameters.AddWithValue("registered_at_utc", productionLot.RegisteredAtUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> TryAddAsync(
        Carrier carrier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ProductionMaterialRegistrationGuard.RequireInitial(carrier);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO olo_carriers (
                carrier_id, carrier_type_id, capacity, document_json, revision, updated_at_utc)
            VALUES (
                @carrier_id, @carrier_type_id, @capacity, @document_json::jsonb, 0, @updated_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("carrier_id", carrier.Id.Value);
        command.Parameters.AddWithValue("carrier_type_id", carrier.CarrierTypeId);
        command.Parameters.AddWithValue("capacity", carrier.Capacity);
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(carrier)));
        command.Parameters.AddWithValue("updated_at_utc", carrier.LastTransitionAtUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> TryAddAsync(
        SlotOccupancy slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ProductionMaterialRegistrationGuard.RequireInitial(slot);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO olo_slot_occupancies (
                line_id, station_system_id, slot_id, document_json, revision,
                status, material_kind, material_id, updated_at_utc)
            VALUES (
                @line_id, @station_system_id, @slot_id, @document_json::jsonb, 0,
                @status, @material_kind, @material_id, @updated_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        AddSlotIdentity(command, slot.Address);
        AddSlotState(command, slot);
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(slot)));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> TryAddAsync(
        MaterialGenealogyLink link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_material_genealogy_links (
                link_id, parent_unit_id, child_unit_id, relationship, operation_id,
                linked_by, document_json, linked_at_utc)
            VALUES (
                @link_id, @parent_unit_id, @child_unit_id, @relationship, @operation_id,
                @linked_by, @document_json::jsonb, @linked_at_utc)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("link_id", link.Id.Value);
        command.Parameters.AddWithValue("parent_unit_id", link.ParentUnitId.Value);
        command.Parameters.AddWithValue("child_unit_id", link.ChildUnitId.Value);
        command.Parameters.AddWithValue("relationship", link.Relationship);
        command.Parameters.AddWithValue("operation_id", link.OperationId);
        command.Parameters.AddWithValue("linked_by", link.LinkedBy);
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(link)));
        command.Parameters.AddWithValue("linked_at_utc", link.LinkedAtUtc);
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (!added)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        await InsertTimelineAsync(
                connection,
                transaction,
                ProductionMaterialTimelineEntry.FromGenealogy(link),
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<ProductionMaterialPersistenceEntry<ProductionUnit>?>
        GetProductionUnitAsync(
            ProductionUnitId productionUnitId,
            CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_production_units
            WHERE production_unit_id = @production_unit_id;
            """;
        command.Parameters.AddWithValue("production_unit_id", productionUnitId.Value);
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_production_lots
            WHERE production_lot_id = @production_lot_id;
            """;
        command.Parameters.AddWithValue("production_lot_id", productionLotId.Value);
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_carriers
            WHERE carrier_id = @carrier_id;
            """;
        command.Parameters.AddWithValue("carrier_id", carrierId.Value);
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_slot_occupancies
            WHERE line_id = @line_id
              AND station_system_id = @station_system_id
              AND slot_id = @slot_id;
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_production_units
            ORDER BY updated_at_utc, production_unit_id;
            """;
        var results = new List<ProductionMaterialPersistenceEntry<ProductionUnit>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ProductionMaterialPersistenceEntry<ProductionUnit>(
                DeserializeProductionUnit(reader.GetString(0)),
                reader.GetInt64(1)));
        }

        return results;
    }

    public async ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<Carrier>>>
        ListCarriersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_carriers
            ORDER BY updated_at_utc, carrier_id;
            """;
        var results = new List<ProductionMaterialPersistenceEntry<Carrier>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ProductionMaterialPersistenceEntry<Carrier>(
                DeserializeCarrier(reader.GetString(0)),
                reader.GetInt64(1)));
        }

        return results;
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text, revision
            FROM olo_slot_occupancies
            WHERE (@line_id IS NULL OR line_id = @line_id)
              AND (@station_system_id IS NULL OR station_system_id = @station_system_id)
            ORDER BY line_id, station_system_id, slot_id;
            """;
        command.Parameters.Add("line_id", NpgsqlDbType.Text).Value =
            (object?)lineId ?? DBNull.Value;
        command.Parameters.Add("station_system_id", NpgsqlDbType.Text).Value =
            (object?)stationSystemId ?? DBNull.Value;
        var results = new List<ProductionMaterialPersistenceEntry<SlotOccupancy>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ProductionMaterialPersistenceEntry<SlotOccupancy>(
                DeserializeSlot(reader.GetString(0)),
                reader.GetInt64(1)));
        }

        return results;
    }

    public async ValueTask<IReadOnlyCollection<MaterialGenealogyLink>> ListGenealogyLinksAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text
            FROM olo_material_genealogy_links
            ORDER BY linked_at_utc, link_id;
            """;
        var results = new List<MaterialGenealogyLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(DeserializeGenealogyLink(reader.GetString(0)));
        }

        return results;
    }

    public async ValueTask<IReadOnlyCollection<ProductionMaterialTimelineEntry>> ListTimelineAsync(
        ProductionMaterialTimelineQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var selectors = new List<string>();
        if (query.ProductionUnitId is { } productionUnitId)
        {
            selectors.Add("(production_unit_id = @production_unit_id OR genealogy_parent_unit_id = @production_unit_id OR genealogy_child_unit_id = @production_unit_id)");
            command.Parameters.AddWithValue("production_unit_id", productionUnitId.Value);
        }

        if (query.ProductionRunId is { } productionRunId)
        {
            selectors.Add("production_run_id = @production_run_id");
            command.Parameters.AddWithValue("production_run_id", productionRunId.Value);
        }

        if (query.CarrierId is { } carrierId)
        {
            selectors.Add("carrier_id = @carrier_id");
            command.Parameters.AddWithValue("carrier_id", carrierId.Value);
        }

        var throughPredicate = query.ThroughUtc is null
            ? string.Empty
            : " AND occurred_at_utc <= @through_utc";
        if (query.ThroughUtc is { } throughUtc)
        {
            command.Parameters.AddWithValue("through_utc", throughUtc);
        }

        command.CommandText = "SELECT document_json::text FROM olo_production_material_timeline WHERE ("
            + string.Join(" OR ", selectors)
            + ")"
            + throughPredicate
            + " ORDER BY occurred_at_utc, evidence_id;";
        var result = new List<ProductionMaterialTimelineEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(DeserializeTimeline(reader.GetString(0)));
        }

        return result;
    }

    public async ValueTask CommitAsync(
        ProductionMaterialCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // All writers acquire row locks in one canonical order. Without this ordering,
        // two valid hand-off commits that mention the same resources in opposite request
        // order can deadlock even though their optimistic revisions are independent.
        foreach (var update in commit.ProductionUnits.OrderBy(
                     static update => update.Aggregate.Id.Value))
        {
            await UpdateProductionUnitAsync(connection, transaction, update, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var update in commit.Carriers.OrderBy(
                     static update => update.Aggregate.Id.Value,
                     StringComparer.Ordinal))
        {
            await UpdateCarrierAsync(connection, transaction, update, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var update in commit.Slots
                     .OrderBy(static update => update.Aggregate.Address.LineId, StringComparer.Ordinal)
                     .ThenBy(
                         static update => update.Aggregate.Address.StationSystemId,
                         StringComparer.Ordinal)
                     .ThenBy(
                         static update => update.Aggregate.Address.SlotId,
                         StringComparer.Ordinal))
        {
            await UpdateSlotAsync(connection, transaction, update, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var evidence in commit.Timeline.OrderBy(static entry => entry.EvidenceId))
        {
            await InsertTimelineAsync(connection, transaction, evidence, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateProductionUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionUnitUpdate update,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE olo_production_units
            SET document_json = @document_json::jsonb,
                revision = @next_revision,
                disposition = @disposition,
                updated_at_utc = @updated_at_utc
            WHERE production_unit_id = @production_unit_id
              AND revision = @expected_revision;
            """;
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(update.Aggregate)));
        command.Parameters.AddWithValue("next_revision", checked(update.ExpectedRevision + 1));
        command.Parameters.AddWithValue("disposition", update.Aggregate.Disposition.ToString());
        command.Parameters.AddWithValue("updated_at_utc", update.Aggregate.LastTransitionAtUtc);
        command.Parameters.AddWithValue("production_unit_id", update.Aggregate.Id.Value);
        command.Parameters.AddWithValue("expected_revision", update.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await ThrowProductionUnitConflictAsync(
                    connection,
                    transaction,
                    update.Aggregate.Id,
                    update.ExpectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertTimelineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionMaterialTimelineEntry evidence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_production_material_timeline (
                evidence_id, kind, production_run_id, production_unit_id, carrier_id,
                genealogy_parent_unit_id, genealogy_child_unit_id, document_json, occurred_at_utc)
            VALUES (
                @evidence_id, @kind, @production_run_id, @production_unit_id, @carrier_id,
                @genealogy_parent_unit_id, @genealogy_child_unit_id, @document_json::jsonb,
                @occurred_at_utc);
            """;
        command.Parameters.AddWithValue("evidence_id", evidence.EvidenceId);
        command.Parameters.AddWithValue("kind", evidence.Kind.ToString());
        command.Parameters.Add("production_run_id", NpgsqlDbType.Uuid).Value =
            (object?)evidence.ProductionRunId?.Value ?? DBNull.Value;
        command.Parameters.Add("production_unit_id", NpgsqlDbType.Uuid).Value =
            (object?)evidence.ProductionUnitId?.Value ?? DBNull.Value;
        command.Parameters.Add("carrier_id", NpgsqlDbType.Text).Value =
            (object?)evidence.CarrierId?.Value ?? DBNull.Value;
        command.Parameters.Add("genealogy_parent_unit_id", NpgsqlDbType.Uuid).Value =
            (object?)evidence.Genealogy?.ParentUnitId.Value ?? DBNull.Value;
        command.Parameters.Add("genealogy_child_unit_id", NpgsqlDbType.Uuid).Value =
            (object?)evidence.Genealogy?.ChildUnitId.Value ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(evidence)));
        command.Parameters.AddWithValue("occurred_at_utc", evidence.OccurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateCarrierAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CarrierUpdate update,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE olo_carriers
            SET document_json = @document_json::jsonb,
                revision = @next_revision,
                updated_at_utc = @updated_at_utc
            WHERE carrier_id = @carrier_id
              AND revision = @expected_revision;
            """;
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(update.Aggregate)));
        command.Parameters.AddWithValue("next_revision", checked(update.ExpectedRevision + 1));
        command.Parameters.AddWithValue("updated_at_utc", update.Aggregate.LastTransitionAtUtc);
        command.Parameters.AddWithValue("carrier_id", update.Aggregate.Id.Value);
        command.Parameters.AddWithValue("expected_revision", update.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await ThrowCarrierConflictAsync(
                    connection,
                    transaction,
                    update.Aggregate.Id,
                    update.ExpectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask UpdateSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SlotOccupancyUpdate update,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE olo_slot_occupancies
            SET document_json = @document_json::jsonb,
                revision = @next_revision,
                status = @status,
                material_kind = @material_kind,
                material_id = @material_id,
                updated_at_utc = @updated_at_utc
            WHERE line_id = @line_id
              AND station_system_id = @station_system_id
              AND slot_id = @slot_id
              AND revision = @expected_revision;
            """;
        command.Parameters.AddWithValue(
            "document_json",
            Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(update.Aggregate)));
        command.Parameters.AddWithValue("next_revision", checked(update.ExpectedRevision + 1));
        AddSlotIdentity(command, update.Aggregate.Address);
        AddSlotState(command, update.Aggregate);
        command.Parameters.AddWithValue("expected_revision", update.ExpectedRevision);
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
        ThrowIfDisposed();
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

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask ValidateSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var table in ExpectedSchema)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT column_name, udt_name, is_nullable
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = @table_name
                ORDER BY ordinal_position;
                """;
            command.Parameters.AddWithValue("table_name", table.Key);
            var actual = new List<ExpectedColumn>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                actual.Add(new ExpectedColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    string.Equals(reader.GetString(2), "YES", StringComparison.Ordinal)));
            }

            if (!actual.SequenceEqual(table.Value))
            {
                throw new InvalidDataException(
                    $"PostgreSQL table {table.Key} does not match the only supported "
                    + "Production Material schema.");
            }
        }
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ThrowProductionUnitConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionUnitId id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM olo_production_units
            WHERE production_unit_id = @production_unit_id;
            """;
        command.Parameters.AddWithValue("production_unit_id", id.Value);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                $"Production Unit {id} must be added before it can be updated.");
        }

        throw new ProductionMaterialConcurrencyException(
            "Production Unit",
            id.ToString(),
            expectedRevision);
    }

    private static async ValueTask ThrowCarrierConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CarrierId id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM olo_carriers
            WHERE carrier_id = @carrier_id;
            """;
        command.Parameters.AddWithValue("carrier_id", id.Value);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                $"Carrier {id} must be added before it can be updated.");
        }

        throw new ProductionMaterialConcurrencyException("Carrier", id.Value, expectedRevision);
    }

    private static async ValueTask ThrowSlotConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SlotAddress address,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM olo_slot_occupancies
            WHERE line_id = @line_id
              AND station_system_id = @station_system_id
              AND slot_id = @slot_id;
            """;
        AddSlotIdentity(command, address);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                $"Slot {address} must be added before it can be updated.");
        }

        throw new ProductionMaterialConcurrencyException(
            "Slot",
            address.ToString(),
            expectedRevision);
    }

    private static void AddSlotIdentity(NpgsqlCommand command, SlotAddress address)
    {
        command.Parameters.AddWithValue("line_id", address.LineId);
        command.Parameters.AddWithValue("station_system_id", address.StationSystemId);
        command.Parameters.AddWithValue("slot_id", address.SlotId);
    }

    private static void AddSlotState(NpgsqlCommand command, SlotOccupancy slot)
    {
        command.Parameters.AddWithValue("status", slot.Status.ToString());
        command.Parameters.Add("material_kind", NpgsqlDbType.Text).Value =
            (object?)slot.Material?.Kind.ToString() ?? DBNull.Value;
        command.Parameters.Add("material_id", NpgsqlDbType.Text).Value =
            (object?)slot.Material?.Value ?? DBNull.Value;
        command.Parameters.AddWithValue("updated_at_utc", slot.LastTransitionAtUtc);
    }

    private static string Serialize<T>(T snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

    private static ProductionUnit DeserializeProductionUnit(string document) =>
        ProductionMaterialSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedProductionUnit>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Production Unit document is empty."));

    private static ProductionLot DeserializeProductionLot(string document) =>
        ProductionMaterialSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedProductionLot>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Production Lot document is empty."));

    private static Carrier DeserializeCarrier(string document) =>
        ProductionMaterialSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedCarrier>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Carrier document is empty."));

    private static SlotOccupancy DeserializeSlot(string document) =>
        ProductionMaterialSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedSlotOccupancy>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Slot occupancy document is empty."));

    private static MaterialGenealogyLink DeserializeGenealogyLink(string document) =>
        ProductionMaterialSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedMaterialGenealogyLink>(document, JsonOptions)
            ?? throw new InvalidDataException("Persisted Material genealogy document is empty."));

    private static ProductionMaterialTimelineEntry DeserializeTimeline(string document) =>
        ProductionMaterialSnapshotMapper.ToAggregate(
            JsonSerializer.Deserialize<PersistedProductionMaterialTimelineEntry>(
                document,
                JsonOptions)
            ?? throw new InvalidDataException("Persisted Production Material evidence is empty."));

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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dataSource.Dispose();
        _schemaLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _dataSource.DisposeAsync().ConfigureAwait(false);
        _schemaLock.Dispose();
    }

    private sealed record ExpectedColumn(string Name, string DatabaseType, bool IsNullable);
}
