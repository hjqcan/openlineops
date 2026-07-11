using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(executionPlan);
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO production_runs (
                run_id,
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
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
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
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
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

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
