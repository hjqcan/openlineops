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
                trace_id, production_run_id, production_unit_id, document_json,
                project_id, application_id, project_snapshot_id, topology_id,
                production_line_definition_id, product_model_id,
                production_unit_identity_input_key, production_unit_identity_value,
                lot_id, carrier_id, actor_id, execution_status, judgement, disposition,
                created_at_utc, started_at_utc, completed_at_utc, updated_at_utc)
            VALUES (
                $trace_id, $production_run_id, $production_unit_id, $document_json,
                $project_id, $application_id, $project_snapshot_id, $topology_id,
                $production_line_definition_id, $product_model_id,
                $production_unit_identity_input_key, $production_unit_identity_value,
                $lot_id, $carrier_id, $actor_id, $execution_status, $judgement, $disposition,
                $created_at_utc, $started_at_utc, $completed_at_utc, $updated_at_utc);
            """;
        command.Parameters.AddWithValue("$trace_id", traceRecord.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$production_run_id", traceRecord.ProductionRunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$production_unit_id", traceRecord.ProductionUnitId.Value.ToString("D"));
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$project_id", traceRecord.ProjectId);
        command.Parameters.AddWithValue("$application_id", traceRecord.ApplicationId);
        command.Parameters.AddWithValue("$project_snapshot_id", traceRecord.ProjectSnapshotId);
        command.Parameters.AddWithValue("$topology_id", traceRecord.TopologyId);
        command.Parameters.AddWithValue("$production_line_definition_id", traceRecord.ProductionLineDefinitionId);
        command.Parameters.AddWithValue("$product_model_id", traceRecord.ProductModelId);
        command.Parameters.AddWithValue(
            "$production_unit_identity_input_key",
            traceRecord.ProductionUnitIdentityInputKey);
        command.Parameters.AddWithValue(
            "$production_unit_identity_value",
            traceRecord.ProductionUnitIdentityValue);
        AddOptionalParameter(command, "$lot_id", traceRecord.LotId);
        AddOptionalParameter(command, "$carrier_id", traceRecord.CarrierId);
        command.Parameters.AddWithValue("$actor_id", traceRecord.ActorId.Value);
        command.Parameters.AddWithValue("$execution_status", traceRecord.ExecutionStatus.ToString());
        command.Parameters.AddWithValue("$judgement", traceRecord.Judgement.ToString());
        command.Parameters.AddWithValue("$disposition", traceRecord.Disposition.ToString());
        command.Parameters.AddWithValue("$created_at_utc", FormatTimestamp(traceRecord.CreatedAtUtc));
        AddOptionalParameter(
            command,
            "$started_at_utc",
            traceRecord.StartedAtUtc is null ? null : FormatTimestamp(traceRecord.StartedAtUtc.Value));
        command.Parameters.AddWithValue("$completed_at_utc", FormatTimestamp(traceRecord.CompletedAtUtc));
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var operation in traceRecord.Operations)
        {
            await InsertOperationAsync(
                connection,
                transaction,
                traceRecord.Id,
                operation,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var deviceId in GetDeviceIds(traceRecord))
        {
            await InsertDeviceAsync(
                connection,
                transaction,
                traceRecord.Id,
                deviceId,
                cancellationToken).ConfigureAwait(false);
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

        return new PagedResult<TraceRecord>(records, paging.PageNumber, paging.PageSize, totalCount);
    }

    private static async ValueTask InsertOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TraceRecordId traceRecordId,
        TraceOperationExecution operation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO trace_operation_executions (
                trace_id, operation_run_id, operation_id, attempt, station_system_id, station_id,
                process_definition_id, process_version_id,
                configuration_snapshot_id, recipe_snapshot_id,
                execution_status, judgement, completed_at_utc)
            VALUES (
                $trace_id, $operation_run_id, $operation_id, $attempt, $station_system_id, $station_id,
                $process_definition_id, $process_version_id,
                $configuration_snapshot_id, $recipe_snapshot_id,
                $execution_status, $judgement, $completed_at_utc);
            """;
        command.Parameters.AddWithValue("$trace_id", traceRecordId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_run_id", operation.OperationRunId);
        command.Parameters.AddWithValue("$operation_id", operation.OperationId);
        command.Parameters.AddWithValue("$attempt", operation.Attempt);
        command.Parameters.AddWithValue("$station_system_id", operation.StationSystemId);
        command.Parameters.AddWithValue("$station_id", operation.StationId.Value);
        command.Parameters.AddWithValue("$process_definition_id", operation.ProcessDefinitionId.Value);
        command.Parameters.AddWithValue("$process_version_id", operation.ProcessVersionId.Value);
        command.Parameters.AddWithValue("$configuration_snapshot_id", operation.ConfigurationSnapshotId.Value);
        command.Parameters.AddWithValue("$recipe_snapshot_id", operation.RecipeSnapshotId.Value);
        command.Parameters.AddWithValue("$execution_status", operation.ExecutionStatus.ToString());
        command.Parameters.AddWithValue("$judgement", operation.Judgement.ToString());
        command.Parameters.AddWithValue("$completed_at_utc", FormatTimestamp(operation.CompletedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var resource in operation.FencingTokens)
        {
            await InsertResourceAsync(
                connection,
                transaction,
                traceRecordId,
                operation.OperationRunId,
                resource,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertResourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TraceRecordId traceRecordId,
        string operationRunId,
        TraceResourceFencingToken resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO trace_operation_resources (
                trace_id, operation_run_id, resource_kind, resource_id, fencing_token)
            VALUES ($trace_id, $operation_run_id, $resource_kind, $resource_id, $fencing_token);
            """;
        command.Parameters.AddWithValue("$trace_id", traceRecordId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_run_id", operationRunId);
        command.Parameters.AddWithValue("$resource_kind", resource.ResourceKind);
        command.Parameters.AddWithValue("$resource_id", resource.ResourceId);
        command.Parameters.AddWithValue("$fencing_token", resource.FencingToken);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TraceRecordId traceRecordId,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO trace_devices (trace_id, device_id)
            VALUES ($trace_id, $device_id);
            """;
        command.Parameters.AddWithValue("$trace_id", traceRecordId.Value.ToString("D"));
        command.Parameters.AddWithValue("$device_id", deviceId);
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
                    production_unit_id TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    project_id TEXT NOT NULL,
                    application_id TEXT NOT NULL,
                    project_snapshot_id TEXT NOT NULL,
                    topology_id TEXT NOT NULL,
                    production_line_definition_id TEXT NOT NULL,
                    product_model_id TEXT NOT NULL,
                    production_unit_identity_input_key TEXT NOT NULL,
                    production_unit_identity_value TEXT NOT NULL,
                    lot_id TEXT NULL,
                    carrier_id TEXT NULL,
                    actor_id TEXT NOT NULL,
                    execution_status TEXT NOT NULL,
                    judgement TEXT NOT NULL,
                    disposition TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    started_at_utc TEXT NULL,
                    completed_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS trace_operation_executions (
                    trace_id TEXT NOT NULL,
                    operation_run_id TEXT NOT NULL,
                    operation_id TEXT NOT NULL,
                    attempt INTEGER NOT NULL,
                    station_system_id TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    process_definition_id TEXT NOT NULL,
                    process_version_id TEXT NOT NULL,
                    configuration_snapshot_id TEXT NOT NULL,
                    recipe_snapshot_id TEXT NOT NULL,
                    execution_status TEXT NOT NULL,
                    judgement TEXT NOT NULL,
                    completed_at_utc TEXT NOT NULL,
                    PRIMARY KEY (trace_id, operation_run_id),
                    UNIQUE (trace_id, operation_id, attempt),
                    FOREIGN KEY (trace_id) REFERENCES trace_records(trace_id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS trace_operation_resources (
                    trace_id TEXT NOT NULL,
                    operation_run_id TEXT NOT NULL,
                    resource_kind TEXT NOT NULL,
                    resource_id TEXT NOT NULL,
                    fencing_token INTEGER NOT NULL,
                    PRIMARY KEY (trace_id, operation_run_id, resource_kind, resource_id),
                    FOREIGN KEY (trace_id, operation_run_id)
                        REFERENCES trace_operation_executions(trace_id, operation_run_id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS trace_devices (
                    trace_id TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    PRIMARY KEY (trace_id, device_id),
                    FOREIGN KEY (trace_id) REFERENCES trace_records(trace_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_trace_records_unit_completed
                    ON trace_records(production_unit_id, completed_at_utc, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_records_line_completed
                    ON trace_records(production_line_definition_id, completed_at_utc, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_operations_station
                    ON trace_operation_executions(station_system_id, completed_at_utc, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_resources_identity
                    ON trace_operation_resources(resource_kind, resource_id, trace_id);
                CREATE INDEX IF NOT EXISTS ix_trace_devices_identity
                    ON trace_devices(device_id, trace_id);
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
        Add(filters, query.ProductionUnitId, "production_unit_id = $production_unit_id", "$production_unit_id",
            value => value.ToString("D"));
        Add(filters, query.ProductModelId, "product_model_id = $product_model_id", "$product_model_id");
        Add(filters, query.ProductionUnitIdentityInputKey,
            "production_unit_identity_input_key = $production_unit_identity_input_key",
            "$production_unit_identity_input_key");
        Add(filters, query.ProductionUnitIdentityValue,
            "production_unit_identity_value = $production_unit_identity_value",
            "$production_unit_identity_value");
        Add(filters, query.LotId, "lot_id = $lot_id", "$lot_id");
        Add(filters, query.CarrierId, "carrier_id = $carrier_id", "$carrier_id");
        Add(filters, query.ActorId, "actor_id = $actor_id", "$actor_id");
        Add(filters, query.ExecutionStatus, "execution_status = $execution_status", "$execution_status");
        Add(filters, query.Judgement, "judgement = $judgement", "$judgement");
        Add(filters, query.Disposition, "disposition = $disposition", "$disposition");
        Add(filters, query.ProjectId, "project_id = $project_id", "$project_id");
        Add(filters, query.ApplicationId, "application_id = $application_id", "$application_id");
        Add(filters, query.ProjectSnapshotId, "project_snapshot_id = $project_snapshot_id", "$project_snapshot_id");
        Add(filters, query.TopologyId, "topology_id = $topology_id", "$topology_id");
        Add(filters, query.ProductionLineDefinitionId,
            "production_line_definition_id = $production_line_definition_id",
            "$production_line_definition_id");
        Add(filters, query.DeviceId,
            "EXISTS (SELECT 1 FROM trace_devices device WHERE device.trace_id = trace_records.trace_id "
            + "AND device.device_id = $device_id)",
            "$device_id");
        Add(filters, query.CompletedFromUtc,
            "completed_at_utc >= $completed_from_utc",
            "$completed_from_utc",
            FormatTimestamp);
        Add(filters, query.CompletedToUtc,
            "completed_at_utc <= $completed_to_utc",
            "$completed_to_utc",
            FormatTimestamp);

        var operationConditions = new List<QueryFilter>();
        Add(operationConditions, query.OperationId, "operation.operation_id = $operation_id", "$operation_id");
        Add(operationConditions, query.StationSystemId,
            "operation.station_system_id = $station_system_id",
            "$station_system_id");
        Add(operationConditions, query.StationId, "operation.station_id = $station_id", "$station_id");
        Add(operationConditions, query.ProcessDefinitionId,
            "operation.process_definition_id = $process_definition_id",
            "$process_definition_id");
        Add(operationConditions, query.ProcessVersionId,
            "operation.process_version_id = $process_version_id",
            "$process_version_id");
        Add(operationConditions, query.ConfigurationSnapshotId,
            "operation.configuration_snapshot_id = $configuration_snapshot_id",
            "$configuration_snapshot_id");
        Add(operationConditions, query.RecipeSnapshotId,
            "operation.recipe_snapshot_id = $recipe_snapshot_id",
            "$recipe_snapshot_id");

        var resourceConditions = new List<QueryFilter>();
        Add(resourceConditions, query.ResourceKind,
            "resource.resource_kind = $resource_kind",
            "$resource_kind");
        Add(resourceConditions, query.ResourceId,
            "resource.resource_id = $resource_id",
            "$resource_id");
        if (resourceConditions.Count > 0)
        {
            operationConditions.Add(new QueryFilter(
                "EXISTS (SELECT 1 FROM trace_operation_resources resource "
                + "WHERE resource.trace_id = operation.trace_id "
                + "AND resource.operation_run_id = operation.operation_run_id AND "
                + string.Join(" AND ", resourceConditions.Select(condition => condition.Sql))
                + ")",
                resourceConditions.SelectMany(condition => condition.Parameters).ToArray()));
        }

        if (operationConditions.Count > 0)
        {
            filters.Add(new QueryFilter(
                "EXISTS (SELECT 1 FROM trace_operation_executions operation "
                + "WHERE operation.trace_id = trace_records.trace_id AND "
                + string.Join(" AND ", operationConditions.Select(condition => condition.Sql))
                + ")",
                operationConditions.SelectMany(condition => condition.Parameters).ToArray()));
        }

        return filters;
    }

    private static IEnumerable<string> GetDeviceIds(TraceRecord record)
    {
        return record.Operations
            .SelectMany(operation => operation.Measurements.Select(measurement => measurement.DeviceId?.Value)
                .Concat(operation.Artifacts.Select(artifact => artifact.DeviceId?.Value)))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal);
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
