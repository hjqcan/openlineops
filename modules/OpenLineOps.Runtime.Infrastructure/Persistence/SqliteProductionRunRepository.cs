using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteProductionRunRepository :
    IProductionRunRepository,
    IProductionRunExecutionPlanRepository,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteProductionRunRepository(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<bool> TryAddAsync(
        ProductionRun run,
        ProductionRunExecutionPlan executionPlan,
        ProductionRunAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(admission);
        if (run.ExecutionStatus != ExecutionStatus.Pending || executionPlan.RunId != run.Id)
        {
            throw new ArgumentException(
                "A new Production Run must be Pending and own its execution plan.",
                nameof(run));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var documentJson = JsonSerializer.Serialize(
            ProductionRunSnapshotMapper.ToSnapshot(run),
            JsonOptions);
        var executionPlanJson = JsonSerializer.Serialize(
            ProductionRunExecutionPlanSnapshotMapper.ToSnapshot(executionPlan),
            JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await TryReserveProductionUnitAsync(
                connection,
                transaction,
                run,
                admission,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO production_runs (
                run_id,
                production_unit_id,
                document_json,
                execution_plan_json,
                revision,
                execution_status,
                project_snapshot_id,
                production_line_definition_id,
                product_model_id,
                identity_input_key,
                identity_value,
                last_transition_at_utc,
                updated_at_utc)
            VALUES (
                $run_id,
                $production_unit_id,
                $document_json,
                $execution_plan_json,
                0,
                $execution_status,
                $project_snapshot_id,
                $production_line_definition_id,
                $product_model_id,
                $identity_input_key,
                $identity_value,
                $last_transition_at_utc,
                $updated_at_utc)
            ON CONFLICT(run_id) DO NOTHING;
            """;
        AddParameters(command, run, documentJson);
        command.Parameters.AddWithValue("$execution_plan_json", executionPlanJson);
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (added)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return added;
    }

    public async ValueTask<long> SaveAsync(
        ProductionRun run,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var documentJson = JsonSerializer.Serialize(
            ProductionRunSnapshotMapper.ToSnapshot(run),
            JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var nextRevision = checked(expectedRevision + 1);
        command.CommandText = """
            UPDATE production_runs
            SET document_json = $document_json,
                revision = $next_revision,
                execution_status = $execution_status,
                project_snapshot_id = $project_snapshot_id,
                production_line_definition_id = $production_line_definition_id,
                product_model_id = $product_model_id,
                identity_input_key = $identity_input_key,
                identity_value = $identity_value,
                last_transition_at_utc = $last_transition_at_utc,
                updated_at_utc = $updated_at_utc
            WHERE run_id = $run_id
              AND revision = $expected_revision;
            """;
        AddParameters(command, run, documentJson);
        command.Parameters.AddWithValue("$expected_revision", expectedRevision);
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await ThrowSaveConflictAsync(
                    connection,
                    transaction,
                    run.Id,
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var productionUnitSynchronization = await SynchronizeProductionUnitAsync(
                connection,
                transaction,
                run,
                nextRevision,
                cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeCompletedSlotsAsync(
                connection,
                transaction,
                run,
                productionUnitSynchronization.Unit,
                cancellationToken)
            .ConfigureAwait(false);
        if (productionUnitSynchronization.PreviousDisposition
            != productionUnitSynchronization.Unit.Disposition)
        {
            await InsertTimelineAsync(
                    connection,
                    transaction,
                    ProductionMaterialTimelineEntry.Disposition(
                        Guid.NewGuid(),
                        productionUnitSynchronization.Unit.Id,
                        run.Id,
                        productionUnitSynchronization.PreviousDisposition,
                        productionUnitSynchronization.Unit.Disposition,
                        productionUnitSynchronization.Unit.DispositionReason,
                        run.ActorId,
                        productionUnitSynchronization.Unit.LastDispositionTransitionAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (run.IsTerminal)
        {
            await InsertTerminalOutboxAsync(
                    connection,
                    transaction,
                    run,
                    documentJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return nextRevision;
    }

    public async ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM production_runs
            WHERE run_id = $run_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProductionRunPersistenceEntry(
                DeserializeRun(reader.GetString(0)),
                reader.GetInt64(1))
            : null;
    }

    public async ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM production_runs
            WHERE execution_status IN ('Pending', 'Running')
            ORDER BY last_transition_at_utc, run_id;
            """;
        var runs = new List<ProductionRunPersistenceEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(new ProductionRunPersistenceEntry(
                DeserializeRun(reader.GetString(0)),
                reader.GetInt64(1)));
        }

        return runs;
    }

    public async ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
        string? productionLineDefinitionId = null,
        string? stationSystemId = null,
        string? slotId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM production_runs
            WHERE execution_status IN ('Pending', 'Running')
            ORDER BY last_transition_at_utc, run_id;
            """;
        var runs = new List<ProductionRunPersistenceEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = new ProductionRunPersistenceEntry(
                DeserializeRun(reader.GetString(0)),
                reader.GetInt64(1));
            if (productionLineDefinitionId is not null
                && !string.Equals(
                    entry.Run.ProductionLineDefinitionId,
                    productionLineDefinitionId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (stationSystemId is not null
                && entry.Run.OperationDefinitions.All(definition =>
                    !string.Equals(
                        definition.StationSystemId,
                        stationSystemId,
                        StringComparison.Ordinal)))
            {
                continue;
            }

            if (slotId is not null
                && entry.Run.OperationDefinitions.All(definition =>
                    definition.ResourceRequirements.All(requirement =>
                        requirement.Kind != ResourceKind.Slot
                        || !string.Equals(requirement.ResourceId, slotId, StringComparison.Ordinal))))
            {
                continue;
            }

            runs.Add(entry);
        }

        return runs;
    }

    public async ValueTask<ProductionRunExecutionPlan?> GetByRunIdAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT execution_plan_json
            FROM production_runs
            WHERE run_id = $run_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string json)
        {
            return null;
        }

        var snapshot = JsonSerializer.Deserialize<PersistedProductionRunExecutionPlan>(json, JsonOptions)
            ?? throw new InvalidDataException("Persisted Production Run execution plan is empty.");
        return ProductionRunExecutionPlanSnapshotMapper.ToAggregate(snapshot);
    }

    public async ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>
        ListPendingTerminalOutboxAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, attempt_count, last_error
            FROM production_run_terminal_outbox
            ORDER BY occurred_at_utc, run_id
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var items = new List<ProductionRunTerminalOutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ProductionRunTerminalOutboxItem(
                DeserializeRun(reader.GetString(0)).ToSnapshot(),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return items;
    }

    public async ValueTask MarkTerminalOutboxProcessedAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM production_run_terminal_outbox WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId.Value.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Production Run terminal outbox item {runId} does not exist.");
        }
    }

    public async ValueTask RecordTerminalOutboxFailureAsync(
        ProductionRunId runId,
        string failureDescription,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDescription);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE production_run_terminal_outbox
            SET attempt_count = attempt_count + 1,
                last_error = $last_error
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value.ToString("D"));
        command.Parameters.AddWithValue(
            "$last_error",
            failureDescription.Length <= 4096
                ? failureDescription
                : failureDescription[..4096]);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Production Run terminal outbox item {runId} does not exist.");
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
                CREATE TABLE IF NOT EXISTS production_runs (
                    run_id TEXT NOT NULL PRIMARY KEY,
                    production_unit_id TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    execution_plan_json TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    execution_status TEXT NOT NULL,
                    project_snapshot_id TEXT NOT NULL,
                    production_line_definition_id TEXT NOT NULL,
                    product_model_id TEXT NOT NULL,
                    identity_input_key TEXT NOT NULL,
                    identity_value TEXT NOT NULL,
                    last_transition_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_production_runs_recovery
                    ON production_runs(execution_status, last_transition_at_utc);

                CREATE UNIQUE INDEX IF NOT EXISTS uq_production_runs_active_unit
                    ON production_runs(production_unit_id)
                    WHERE execution_status IN ('Pending', 'Running');

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

                CREATE TABLE IF NOT EXISTS production_run_terminal_outbox (
                    run_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    last_error TEXT NULL,
                    FOREIGN KEY (run_id) REFERENCES production_runs(run_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_production_run_terminal_outbox_order
                    ON production_run_terminal_outbox(occurred_at_utc, run_id);

                CREATE TABLE IF NOT EXISTS production_material_timeline (
                    evidence_id TEXT NOT NULL PRIMARY KEY,
                    kind TEXT NOT NULL,
                    production_run_id TEXT NULL,
                    operation_run_id TEXT NULL,
                    slot_fencing_token INTEGER NULL,
                    production_unit_id TEXT NULL,
                    carrier_id TEXT NULL,
                    genealogy_parent_unit_id TEXT NULL,
                    genealogy_child_unit_id TEXT NULL,
                    document_json TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    CHECK (
                        (operation_run_id IS NULL AND slot_fencing_token IS NULL)
                        OR (kind = 'SlotOccupancyTransition'
                            AND production_run_id IS NOT NULL
                            AND operation_run_id IS NOT NULL
                            AND operation_run_id <> ''
                            AND operation_run_id = trim(operation_run_id)
                            AND slot_fencing_token > 0))
                );

                CREATE INDEX IF NOT EXISTS ix_production_material_timeline_slot_completion
                    ON production_material_timeline(
                        production_run_id, operation_run_id, slot_fencing_token, occurred_at_utc);
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
                "Runtime SQLite persistence requires a file-backed database; use the InMemory provider for transient execution.",
                nameof(connectionString));
        }

        return normalized;
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
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

    private static ProductionRun DeserializeRun(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedProductionRun>(documentJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted production run document is empty.");
        return ProductionRunSnapshotMapper.ToAggregate(snapshot);
    }

    private static async ValueTask ThrowSaveConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRunId runId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM production_runs WHERE run_id = $run_id LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", runId.Value.ToString("D"));
        var storedRevision = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (storedRevision is null)
        {
            throw new InvalidOperationException(
                $"Production run {runId} must be added before it can be updated.");
        }

        throw new ProductionRunConcurrencyException(runId, expectedRevision);
    }

    private static async ValueTask<bool> TryReserveProductionUnitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRun run,
        ProductionRunAdmission admission,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT document_json, revision
            FROM production_units
            WHERE production_unit_id = $production_unit_id
            LIMIT 1;
            """;
        read.Parameters.AddWithValue("$production_unit_id", run.ProductionUnitId.Value.ToString("D"));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var storedDocument = reader.GetString(0);
        var storedRevision = reader.GetInt64(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        var unit = DeserializeProductionUnit(storedDocument);
        if (storedRevision != admission.ExpectedRevision
            || unit.ToSnapshot() != admission.ProductionUnit
            || unit.Id != run.ProductionUnitId)
        {
            return false;
        }

        var reservation = unit.ReserveProductionRun(run.Id, run.CreatedAtUtc);
        if (!reservation.Succeeded)
        {
            return false;
        }

        return await UpdateProductionUnitAsync(
                connection,
                transaction,
                unit,
                storedRevision,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<ProductionUnitSynchronization> SynchronizeProductionUnitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRun run,
        long runRevision,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT document_json, revision
            FROM production_units
            WHERE production_unit_id = $production_unit_id
            LIMIT 1;
            """;
        read.Parameters.AddWithValue("$production_unit_id", run.ProductionUnitId.Value.ToString("D"));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                $"Production Unit {run.ProductionUnitId} disappeared during Production Run {run.Id}.");
        }

        var unit = DeserializeProductionUnit(reader.GetString(0));
        var unitRevision = reader.GetInt64(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        var previousDisposition = unit.Disposition;
        var result = unit.SynchronizeProductionRun(
            run.Id,
            runRevision,
            run.Disposition,
            run.IsTerminal,
            run.FailureReason,
            run.LastTransitionAtUtc);
        if (!result.Succeeded
            || !await UpdateProductionUnitAsync(
                    connection,
                    transaction,
                    unit,
                    unitRevision,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidDataException(
                result.Succeeded
                    ? $"Production Unit {unit.Id} changed during atomic run synchronization."
                    : result.Message);
        }

        return new ProductionUnitSynchronization(unit, previousDisposition);
    }

    private static async ValueTask SynchronizeCompletedSlotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRun run,
        ProductionUnit unit,
        CancellationToken cancellationToken)
    {
        var expectedMaterial = unit.Location is
        {
            Kind: MaterialLocationKind.CarrierPosition,
            CarrierId: { } carrierId
        }
            ? MaterialReference.ForCarrier(carrierId)
            : MaterialReference.ForProductionUnit(unit.Id);
        foreach (var completedSlot in ProductionRunSlotLifecycle.ResolveCompletedSlots(run))
        {
            if (await HasSlotCompletionEvidenceAsync(
                    connection,
                    transaction,
                    run.Id,
                    completedSlot,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT document_json, revision
                FROM slot_occupancies
                WHERE line_id = $line_id
                  AND station_system_id = $station_system_id
                  AND slot_id = $slot_id
                LIMIT 1;
                """;
            AddSlotIdentity(read, completedSlot.Address);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    $"Resolved Slot {completedSlot.Address} disappeared during run synchronization.");
            }

            var slot = DeserializeSlot(reader.GetString(0));
            var revision = reader.GetInt64(1);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (slot.Material != expectedMaterial)
            {
                throw new InvalidDataException(
                    $"Resolved Slot {slot.Address} is bound to {slot.Material}, not {expectedMaterial}.");
            }

            if (slot.Status != SlotOccupancyStatus.Running)
            {
                throw new InvalidDataException(
                    $"Resolved Slot {slot.Address} has no exact completion evidence for Operation Run "
                    + $"{completedSlot.OperationRunId} at fencing token {completedSlot.FencingToken}; "
                    + $"its status must be Running, not {slot.Status}.");
            }

            var completed = slot.Complete(expectedMaterial, completedSlot.CompletedAtUtc);
            if (!completed.Succeeded)
            {
                throw new InvalidDataException(completed.Message);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
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
            update.Parameters.AddWithValue(
                "$document_json",
                JsonSerializer.Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(slot), JsonOptions));
            update.Parameters.AddWithValue("$next_revision", checked(revision + 1));
            update.Parameters.AddWithValue("$status", slot.Status.ToString());
            update.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(slot.LastTransitionAtUtc));
            AddSlotIdentity(update, slot.Address);
            update.Parameters.AddWithValue("$expected_revision", revision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException(
                    $"Resolved Slot {slot.Address} changed during atomic run synchronization.");
            }

            await InsertTimelineAsync(
                    connection,
                    transaction,
                    ProductionMaterialTimelineEntry.SlotOccupancy(
                        Guid.NewGuid(),
                        slot.Address,
                        expectedMaterial,
                        run.Id,
                        completedSlot.OperationRunId,
                        completedSlot.FencingToken,
                        SlotOccupancyStatus.Running,
                        SlotOccupancyStatus.Occupied,
                        run.ActorId,
                        completedSlot.CompletedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<bool> HasSlotCompletionEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRunId runId,
        CompletedSlotOperation completedSlot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json
            FROM production_material_timeline
            WHERE kind = $kind
              AND production_run_id = $production_run_id
              AND operation_run_id = $operation_run_id
              AND slot_fencing_token = $slot_fencing_token
              AND occurred_at_utc = $occurred_at_utc;
            """;
        command.Parameters.AddWithValue(
            "$kind",
            ProductionMaterialEvidenceKind.SlotOccupancyTransition.ToString());
        command.Parameters.AddWithValue("$production_run_id", runId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_run_id", completedSlot.OperationRunId);
        command.Parameters.AddWithValue("$slot_fencing_token", completedSlot.FencingToken);
        command.Parameters.AddWithValue(
            "$occurred_at_utc",
            FormatTimestamp(completedSlot.CompletedAtUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = JsonSerializer.Deserialize<PersistedProductionMaterialTimelineEntry>(
                reader.GetString(0),
                JsonOptions)
                ?? throw new InvalidDataException(
                    "Persisted Slot completion evidence is empty.");
            if (ProductionRunSlotLifecycle.IsCompletionEvidence(
                    ProductionMaterialSnapshotMapper.ToAggregate(snapshot),
                    runId,
                    completedSlot))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask InsertTimelineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionMaterialTimelineEntry evidence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO production_material_timeline (
                evidence_id, kind, production_run_id, operation_run_id, slot_fencing_token,
                production_unit_id, carrier_id,
                genealogy_parent_unit_id, genealogy_child_unit_id, document_json, occurred_at_utc)
            VALUES (
                $evidence_id, $kind, $production_run_id, $operation_run_id, $slot_fencing_token,
                $production_unit_id, $carrier_id,
                $genealogy_parent_unit_id, $genealogy_child_unit_id, $document_json, $occurred_at_utc);
            """;
        command.Parameters.AddWithValue("$evidence_id", evidence.EvidenceId.ToString("D"));
        command.Parameters.AddWithValue("$kind", evidence.Kind.ToString());
        command.Parameters.AddWithValue(
            "$production_run_id",
            (object?)evidence.ProductionRunId?.Value.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$operation_run_id",
            (object?)evidence.OperationRunId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$slot_fencing_token",
            (object?)evidence.SlotFencingToken ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$production_unit_id",
            (object?)evidence.ProductionUnitId?.Value.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$carrier_id", (object?)evidence.CarrierId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$genealogy_parent_unit_id",
            (object?)evidence.Genealogy?.ParentUnitId.Value.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$genealogy_child_unit_id",
            (object?)evidence.Genealogy?.ChildUnitId.Value.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$document_json",
            JsonSerializer.Serialize(
                ProductionMaterialSnapshotMapper.ToSnapshot(evidence),
                JsonOptions));
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(evidence.OccurredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record ProductionUnitSynchronization(
        ProductionUnit Unit,
        ProductDisposition PreviousDisposition);

    private static async ValueTask<bool> UpdateProductionUnitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionUnit unit,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE production_units
            SET document_json = $document_json,
                revision = $next_revision,
                disposition = $disposition,
                updated_at_utc = $updated_at_utc
            WHERE production_unit_id = $production_unit_id
              AND revision = $expected_revision;
            """;
        update.Parameters.AddWithValue(
            "$document_json",
            JsonSerializer.Serialize(ProductionMaterialSnapshotMapper.ToSnapshot(unit), JsonOptions));
        update.Parameters.AddWithValue("$next_revision", checked(expectedRevision + 1));
        update.Parameters.AddWithValue("$disposition", unit.Disposition.ToString());
        update.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(unit.LastTransitionAtUtc));
        update.Parameters.AddWithValue("$production_unit_id", unit.Id.Value.ToString("D"));
        update.Parameters.AddWithValue("$expected_revision", expectedRevision);
        return await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static ProductionUnit DeserializeProductionUnit(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedProductionUnit>(documentJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted Production Unit document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static SlotOccupancy DeserializeSlot(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedSlotOccupancy>(documentJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted Slot occupancy document is empty.");
        return ProductionMaterialSnapshotMapper.ToAggregate(snapshot);
    }

    private static async ValueTask InsertTerminalOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRun run,
        string documentJson,
        CancellationToken cancellationToken)
    {
        var completedAtUtc = run.CompletedAtUtc
            ?? throw new InvalidOperationException(
                $"Terminal Production Run {run.Id} has no completion timestamp.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO production_run_terminal_outbox (
                run_id,
                document_json,
                occurred_at_utc,
                attempt_count,
                last_error)
            VALUES (
                $run_id,
                $document_json,
                $occurred_at_utc,
                0,
                NULL)
            ON CONFLICT(run_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$run_id", run.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(completedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(
        SqliteCommand command,
        ProductionRun run,
        string documentJson)
    {
        command.Parameters.AddWithValue("$run_id", run.Id.Value.ToString("D"));
        command.Parameters.AddWithValue(
            "$production_unit_id",
            run.ProductionUnitId.Value.ToString("D"));
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$execution_status", run.ExecutionStatus.ToString());
        command.Parameters.AddWithValue("$project_snapshot_id", run.ProjectSnapshotId);
        command.Parameters.AddWithValue(
            "$production_line_definition_id",
            run.ProductionLineDefinitionId);
        command.Parameters.AddWithValue("$product_model_id", run.ProductionUnitIdentity.ModelId);
        command.Parameters.AddWithValue("$identity_input_key", run.ProductionUnitIdentity.InputKey);
        command.Parameters.AddWithValue("$identity_value", run.ProductionUnitIdentity.Value);
        command.Parameters.AddWithValue(
            "$last_transition_at_utc",
            FormatTimestamp(run.LastTransitionAtUtc));
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));
    }

    private static void AddSlotIdentity(SqliteCommand command, SlotAddress address)
    {
        command.Parameters.AddWithValue("$line_id", address.LineId);
        command.Parameters.AddWithValue("$station_system_id", address.StationSystemId);
        command.Parameters.AddWithValue("$slot_id", address.SlotId);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
