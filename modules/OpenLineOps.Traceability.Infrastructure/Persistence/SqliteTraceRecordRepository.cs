using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Infrastructure.Persistence;

public sealed class SqliteTraceRecordRepository : ITraceRecordRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteTraceRecordRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory
            || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Traceability SQLite persistence requires a file-backed database; use the InMemory provider for transient execution.",
                nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    public async ValueTask<bool> TryAddAsync(
        TraceRecord traceRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceRecord);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var documentJson = JsonSerializer.Serialize(
            TraceRecordSnapshotMapper.ToSnapshot(traceRecord),
            JsonOptions);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO trace_records (
                trace_id, production_run_id, document_json,
                project_id, application_id, project_snapshot_id, topology_id,
                production_line_definition_id,
                dut_model_id, dut_identity_input_key, dut_identity_value,
                batch_id, fixture_id, device_id, actor_id,
                run_status, judgement, created_at_utc, started_at_utc,
                completed_at_utc, updated_at_utc)
            VALUES (
                $trace_id, $production_run_id, $document_json,
                $project_id, $application_id, $project_snapshot_id, $topology_id,
                $production_line_definition_id,
                $dut_model_id, $dut_identity_input_key, $dut_identity_value,
                $batch_id, $fixture_id, $device_id, $actor_id,
                $run_status, $judgement, $created_at_utc, $started_at_utc,
                $completed_at_utc, $updated_at_utc);
            """;
        command.Parameters.AddWithValue("$trace_id", traceRecord.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$production_run_id", traceRecord.ProductionRunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$project_id", traceRecord.ProjectId);
        command.Parameters.AddWithValue("$application_id", traceRecord.ApplicationId);
        command.Parameters.AddWithValue("$project_snapshot_id", traceRecord.ProjectSnapshotId);
        command.Parameters.AddWithValue("$topology_id", traceRecord.TopologyId);
        command.Parameters.AddWithValue("$production_line_definition_id", traceRecord.ProductionLineDefinitionId);
        command.Parameters.AddWithValue("$dut_model_id", traceRecord.DutModelId);
        command.Parameters.AddWithValue("$dut_identity_input_key", traceRecord.DutIdentityInputKey);
        command.Parameters.AddWithValue("$dut_identity_value", traceRecord.DutIdentityValue);
        AddOptionalParameter(command, "$batch_id", traceRecord.BatchId);
        AddOptionalParameter(command, "$fixture_id", traceRecord.FixtureId);
        AddOptionalParameter(command, "$device_id", traceRecord.DeviceId);
        command.Parameters.AddWithValue("$actor_id", traceRecord.ActorId.Value);
        command.Parameters.AddWithValue("$run_status", traceRecord.RunStatus.ToString());
        command.Parameters.AddWithValue("$judgement", traceRecord.Judgement.ToString());
        command.Parameters.AddWithValue("$created_at_utc", FormatTimestamp(traceRecord.CreatedAtUtc));
        AddOptionalParameter(
            command,
            "$started_at_utc",
            traceRecord.StartedAtUtc is null ? null : FormatTimestamp(traceRecord.StartedAtUtc.Value));
        command.Parameters.AddWithValue("$completed_at_utc", FormatTimestamp(traceRecord.CompletedAtUtc));
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var stage in traceRecord.Stages)
        {
            await InsertStageAsync(connection, transaction, traceRecord.Id, stage, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<TraceRecord?> GetByIdAsync(
        TraceRecordId traceRecordId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_json FROM trace_records WHERE trace_id = $trace_id LIMIT 1;";
        command.Parameters.AddWithValue("$trace_id", traceRecordId.Value.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : Deserialize((string)value);
    }

    public async ValueTask<PagedResult<TraceRecord>> QueryAsync(
        TraceRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var paging = query.Paging.Normalize(TraceRecordQuery.MaxPageSize);
        var filters = BuildFilters(query);
        var where = filters.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", filters.Select(filter => filter.Sql));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM trace_records {where};";
        AddQueryParameters(countCommand, filters);
        var totalCount = Convert.ToInt64(
            await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        await using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT document_json
            FROM trace_records
            {where}
            ORDER BY completed_at_utc, trace_id
            LIMIT $take OFFSET $skip;
            """;
        AddQueryParameters(pageCommand, filters);
        pageCommand.Parameters.AddWithValue("$take", paging.PageSize);
        pageCommand.Parameters.AddWithValue("$skip", paging.Skip);
        var records = new List<TraceRecord>();
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(Deserialize(reader.GetString(0)));
        }

        return new PagedResult<TraceRecord>(
            records,
            paging.PageNumber,
            paging.PageSize,
            totalCount);
    }

    private static async ValueTask InsertStageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TraceRecordId traceRecordId,
        TraceStageExecution stage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO trace_stage_executions (
                trace_id, stage_id, sequence, workstation_id, station_id,
                process_definition_id, process_version_id,
                configuration_snapshot_id, recipe_snapshot_id, runtime_session_id,
                stage_status, completed_at_utc)
            VALUES (
                $trace_id, $stage_id, $sequence, $workstation_id, $station_id,
                $process_definition_id, $process_version_id,
                $configuration_snapshot_id, $recipe_snapshot_id, $runtime_session_id,
                $stage_status, $completed_at_utc);
            """;
        command.Parameters.AddWithValue("$trace_id", traceRecordId.Value.ToString("D"));
        command.Parameters.AddWithValue("$stage_id", stage.StageId);
        command.Parameters.AddWithValue("$sequence", stage.Sequence);
        command.Parameters.AddWithValue("$workstation_id", stage.WorkstationId);
        command.Parameters.AddWithValue("$station_id", stage.StationId.Value);
        command.Parameters.AddWithValue("$process_definition_id", stage.ProcessDefinitionId.Value);
        command.Parameters.AddWithValue("$process_version_id", stage.ProcessVersionId.Value);
        command.Parameters.AddWithValue("$configuration_snapshot_id", stage.ConfigurationSnapshotId.Value);
        command.Parameters.AddWithValue("$recipe_snapshot_id", stage.RecipeSnapshotId.Value);
        AddOptionalParameter(
            command,
            "$runtime_session_id",
            stage.RuntimeSessionId?.Value.ToString("D"));
        command.Parameters.AddWithValue("$stage_status", stage.Status.ToString());
        command.Parameters.AddWithValue("$completed_at_utc", FormatTimestamp(stage.CompletedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS trace_records (
                    trace_id TEXT NOT NULL PRIMARY KEY,
                    production_run_id TEXT NOT NULL UNIQUE,
                    document_json TEXT NOT NULL,
                    project_id TEXT NOT NULL,
                    application_id TEXT NOT NULL,
                    project_snapshot_id TEXT NOT NULL,
                    topology_id TEXT NOT NULL,
                    production_line_definition_id TEXT NOT NULL,
                    dut_model_id TEXT NOT NULL,
                    dut_identity_input_key TEXT NOT NULL,
                    dut_identity_value TEXT NOT NULL,
                    batch_id TEXT NULL,
                    fixture_id TEXT NULL,
                    device_id TEXT NULL,
                    actor_id TEXT NOT NULL,
                    run_status TEXT NOT NULL,
                    judgement TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    started_at_utc TEXT NULL,
                    completed_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS trace_stage_executions (
                    trace_id TEXT NOT NULL,
                    stage_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    workstation_id TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    process_definition_id TEXT NOT NULL,
                    process_version_id TEXT NOT NULL,
                    configuration_snapshot_id TEXT NOT NULL,
                    recipe_snapshot_id TEXT NOT NULL,
                    runtime_session_id TEXT NULL,
                    stage_status TEXT NOT NULL,
                    completed_at_utc TEXT NOT NULL,
                    PRIMARY KEY (trace_id, stage_id),
                    UNIQUE (trace_id, sequence),
                    FOREIGN KEY (trace_id) REFERENCES trace_records(trace_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_trace_records_dut_completed
                    ON trace_records(dut_identity_value, completed_at_utc, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_records_batch_completed
                    ON trace_records(batch_id, completed_at_utc, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_records_project_snapshot_completed
                    ON trace_records(project_snapshot_id, completed_at_utc, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_records_line_completed
                    ON trace_records(production_line_definition_id, completed_at_utc, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_records_judgement_completed
                    ON trace_records(judgement, completed_at_utc, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_stage_station
                    ON trace_stage_executions(station_id, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_stage_process
                    ON trace_stage_executions(process_version_id, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_stage_configuration
                    ON trace_stage_executions(configuration_snapshot_id, trace_id);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_trace_stage_runtime_session
                    ON trace_stage_executions(runtime_session_id)
                    WHERE runtime_session_id IS NOT NULL;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private void EnsureDatabaseDirectory()
    {
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static List<QueryFilter> BuildFilters(TraceRecordQuery query)
    {
        var filters = new List<QueryFilter>();
        Add(filters, query.ProductionRunId, "production_run_id = $production_run_id", "$production_run_id",
            value => value.ToString("D"));
        Add(filters, query.DutModelId, "dut_model_id = $dut_model_id", "$dut_model_id");
        Add(filters, query.DutIdentityInputKey, "dut_identity_input_key = $dut_identity_input_key", "$dut_identity_input_key");
        Add(filters, query.DutIdentityValue, "dut_identity_value = $dut_identity_value", "$dut_identity_value");
        Add(filters, query.BatchId, "batch_id = $batch_id", "$batch_id");
        Add(filters, query.FixtureId, "fixture_id = $fixture_id", "$fixture_id");
        Add(filters, query.DeviceId, "device_id = $device_id", "$device_id");
        Add(filters, query.ActorId, "actor_id = $actor_id", "$actor_id");
        Add(filters, query.RunStatus, "run_status = $run_status", "$run_status");
        Add(filters, query.Judgement, "judgement = $judgement", "$judgement");
        Add(filters, query.ProjectId, "project_id = $project_id", "$project_id");
        Add(filters, query.ApplicationId, "application_id = $application_id", "$application_id");
        Add(filters, query.ProjectSnapshotId, "project_snapshot_id = $project_snapshot_id", "$project_snapshot_id");
        Add(filters, query.TopologyId, "topology_id = $topology_id", "$topology_id");
        Add(filters, query.ProductionLineDefinitionId,
            "production_line_definition_id = $production_line_definition_id", "$production_line_definition_id");
        Add(filters, query.CompletedFromUtc, "completed_at_utc >= $completed_from_utc", "$completed_from_utc", FormatTimestamp);
        Add(filters, query.CompletedToUtc, "completed_at_utc <= $completed_to_utc", "$completed_to_utc", FormatTimestamp);

        var stageConditions = new List<QueryFilter>();
        Add(stageConditions, query.StageId, "stage.stage_id = $stage_id", "$stage_id");
        Add(stageConditions, query.WorkstationId, "stage.workstation_id = $workstation_id", "$workstation_id");
        Add(stageConditions, query.StationId, "stage.station_id = $station_id", "$station_id");
        Add(stageConditions, query.ProcessDefinitionId,
            "stage.process_definition_id = $process_definition_id", "$process_definition_id");
        Add(stageConditions, query.ProcessVersionId,
            "stage.process_version_id = $process_version_id", "$process_version_id");
        Add(stageConditions, query.ConfigurationSnapshotId,
            "stage.configuration_snapshot_id = $configuration_snapshot_id", "$configuration_snapshot_id");
        Add(stageConditions, query.RecipeSnapshotId,
            "stage.recipe_snapshot_id = $recipe_snapshot_id", "$recipe_snapshot_id");
        if (stageConditions.Count > 0)
        {
            filters.Add(new QueryFilter(
                "EXISTS (SELECT 1 FROM trace_stage_executions stage "
                + "WHERE stage.trace_id = trace_records.trace_id AND "
                + string.Join(" AND ", stageConditions.Select(condition => condition.Sql))
                + ")",
                stageConditions.SelectMany(condition => condition.Parameters).ToArray()));
        }

        return filters;
    }

    private static void Add<T>(
        ICollection<QueryFilter> filters,
        T? value,
        string sql,
        string parameterName,
        Func<T, object>? map = null)
        where T : struct
    {
        if (value is not null)
        {
            filters.Add(new QueryFilter(
                sql,
                [new QueryParameter(parameterName, map is null ? value.Value : map(value.Value))]));
        }
    }

    private static void Add(
        ICollection<QueryFilter> filters,
        string? value,
        string sql,
        string parameterName)
    {
        if (value is not null)
        {
            filters.Add(new QueryFilter(sql, [new QueryParameter(parameterName, value)]));
        }
    }

    private static void AddQueryParameters(SqliteCommand command, IEnumerable<QueryFilter> filters)
    {
        foreach (var parameter in filters.SelectMany(filter => filter.Parameters))
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private static void AddOptionalParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static TraceRecord Deserialize(string json)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedTraceRecord>(json, JsonOptions)
            ?? throw new InvalidOperationException("Persisted trace record document is empty.");
        return TraceRecordSnapshotMapper.ToAggregate(snapshot);
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString)
        {
            ForeignKeys = true
        };
        return new SqliteConnection(builder.ToString());
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    public void Dispose() => _schemaLock.Dispose();

    private sealed record QueryParameter(string Name, object Value);
    private sealed record QueryFilter(string Sql, IReadOnlyCollection<QueryParameter> Parameters);
}
