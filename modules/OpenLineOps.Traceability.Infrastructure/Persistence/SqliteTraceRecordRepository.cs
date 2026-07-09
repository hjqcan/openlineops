using System.Globalization;
using System.Text;
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
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public async ValueTask SaveAsync(
        TraceRecord traceRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceRecord);

        await EnsureSchemaAsync(cancellationToken);

        var snapshot = TraceRecordSnapshotMapper.ToSnapshot(traceRecord);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trace_records (
                trace_id,
                document_json,
                runtime_session_id,
                serial_number,
                batch_id,
                station_id,
                fixture_id,
                process_definition_id,
                process_version_id,
                configuration_snapshot_id,
                recipe_snapshot_id,
                device_id,
                judgement,
                started_at_utc,
                completed_at_utc,
                recorded_by,
                updated_at_utc)
            VALUES (
                $trace_id,
                $document_json,
                $runtime_session_id,
                $serial_number,
                $batch_id,
                $station_id,
                $fixture_id,
                $process_definition_id,
                $process_version_id,
                $configuration_snapshot_id,
                $recipe_snapshot_id,
                $device_id,
                $judgement,
                $started_at_utc,
                $completed_at_utc,
                $recorded_by,
                $updated_at_utc)
            ON CONFLICT(trace_id) DO UPDATE SET
                document_json = excluded.document_json,
                runtime_session_id = excluded.runtime_session_id,
                serial_number = excluded.serial_number,
                batch_id = excluded.batch_id,
                station_id = excluded.station_id,
                fixture_id = excluded.fixture_id,
                process_definition_id = excluded.process_definition_id,
                process_version_id = excluded.process_version_id,
                configuration_snapshot_id = excluded.configuration_snapshot_id,
                recipe_snapshot_id = excluded.recipe_snapshot_id,
                device_id = excluded.device_id,
                judgement = excluded.judgement,
                started_at_utc = excluded.started_at_utc,
                completed_at_utc = excluded.completed_at_utc,
                recorded_by = excluded.recorded_by,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$trace_id", traceRecord.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$runtime_session_id", traceRecord.RuntimeSessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$serial_number", traceRecord.SerialNumber);
        AddOptionalParameter(command, "$batch_id", traceRecord.BatchId);
        command.Parameters.AddWithValue("$station_id", traceRecord.StationId.Value);
        AddOptionalParameter(command, "$fixture_id", traceRecord.FixtureId);
        command.Parameters.AddWithValue("$process_definition_id", traceRecord.ProcessDefinitionId.Value);
        command.Parameters.AddWithValue("$process_version_id", traceRecord.ProcessVersionId.Value);
        command.Parameters.AddWithValue("$configuration_snapshot_id", traceRecord.ConfigurationSnapshotId.Value);
        command.Parameters.AddWithValue("$recipe_snapshot_id", traceRecord.RecipeSnapshotId.Value);
        command.Parameters.AddWithValue("$device_id", traceRecord.DeviceId.Value);
        command.Parameters.AddWithValue("$judgement", traceRecord.Judgement.ToString());
        command.Parameters.AddWithValue("$started_at_utc", FormatTimestamp(traceRecord.StartedAtUtc));
        command.Parameters.AddWithValue("$completed_at_utc", FormatTimestamp(traceRecord.CompletedAtUtc));
        command.Parameters.AddWithValue("$recorded_by", traceRecord.RecordedBy.Value);
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<TraceRecord?> GetByIdAsync(
        TraceRecordId traceRecordId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM trace_records
            WHERE trace_id = $trace_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$trace_id", traceRecordId.Value.ToString("D"));

        var documentJson = await command.ExecuteScalarAsync(cancellationToken);
        return documentJson is null
            ? null
            : DeserializeTraceRecord((string)documentJson);
    }

    public async ValueTask<PagedResult<TraceRecord>> QueryAsync(
        TraceRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await EnsureSchemaAsync(cancellationToken);

        var paging = query.Paging.Normalize(TraceRecordQuery.MaxPageSize);
        var filters = BuildFilters(query);
        var whereClause = filters.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", filters.Select(filter => filter.Sql));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var totalCount = await CountAsync(connection, whereClause, filters, cancellationToken);
        var records = await QueryPageAsync(connection, whereClause, filters, paging, cancellationToken);

        return new PagedResult<TraceRecord>(
            records,
            paging.PageNumber,
            paging.PageSize,
            totalCount);
    }

    private static async ValueTask<long> CountAsync(
        SqliteConnection connection,
        string whereClause,
        IReadOnlyCollection<QueryFilter> filters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM trace_records {whereClause};";
        AddQueryParameters(command, filters);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<IReadOnlyCollection<TraceRecord>> QueryPageAsync(
        SqliteConnection connection,
        string whereClause,
        IReadOnlyCollection<QueryFilter> filters,
        PagedRequest paging,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT document_json
            FROM trace_records
            {whereClause}
            ORDER BY completed_at_utc, trace_id
            LIMIT $take OFFSET $skip;
            """;
        AddQueryParameters(command, filters);
        command.Parameters.AddWithValue("$take", paging.PageSize);
        command.Parameters.AddWithValue("$skip", paging.Skip);

        var records = new List<TraceRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(DeserializeTraceRecord(reader.GetString(0)));
        }

        return records;
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            EnsureDatabaseDirectory();

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS trace_records (
                    trace_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    runtime_session_id TEXT NOT NULL,
                    serial_number TEXT NOT NULL,
                    batch_id TEXT NULL,
                    station_id TEXT NOT NULL,
                    fixture_id TEXT NULL,
                    process_definition_id TEXT NOT NULL,
                    process_version_id TEXT NOT NULL,
                    configuration_snapshot_id TEXT NOT NULL,
                    recipe_snapshot_id TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    judgement TEXT NOT NULL,
                    started_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT NOT NULL,
                    recorded_by TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_trace_records_serial_completed
                    ON trace_records(serial_number, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_batch_completed
                    ON trace_records(batch_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_station_completed
                    ON trace_records(station_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_fixture_completed
                    ON trace_records(fixture_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_runtime_session
                    ON trace_records(runtime_session_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_process_completed
                    ON trace_records(process_version_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_device_completed
                    ON trace_records(device_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_judgement_completed
                    ON trace_records(judgement, completed_at_utc, trace_id);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static List<QueryFilter> BuildFilters(TraceRecordQuery query)
    {
        var filters = new List<QueryFilter>();

        if (query.SerialNumber is not null)
        {
            filters.Add(new QueryFilter("serial_number = $serial_number", "$serial_number", query.SerialNumber));
        }

        if (query.BatchId is not null)
        {
            filters.Add(new QueryFilter("batch_id = $batch_id", "$batch_id", query.BatchId));
        }

        if (query.StationId is not null)
        {
            filters.Add(new QueryFilter("station_id = $station_id", "$station_id", query.StationId));
        }

        if (query.FixtureId is not null)
        {
            filters.Add(new QueryFilter("fixture_id = $fixture_id", "$fixture_id", query.FixtureId));
        }

        if (query.ProcessDefinitionId is not null)
        {
            filters.Add(new QueryFilter(
                "process_definition_id = $process_definition_id",
                "$process_definition_id",
                query.ProcessDefinitionId));
        }

        if (query.ProcessVersionId is not null)
        {
            filters.Add(new QueryFilter(
                "process_version_id = $process_version_id",
                "$process_version_id",
                query.ProcessVersionId));
        }

        if (query.ConfigurationSnapshotId is not null)
        {
            filters.Add(new QueryFilter(
                "configuration_snapshot_id = $configuration_snapshot_id",
                "$configuration_snapshot_id",
                query.ConfigurationSnapshotId));
        }

        if (query.RecipeSnapshotId is not null)
        {
            filters.Add(new QueryFilter(
                "recipe_snapshot_id = $recipe_snapshot_id",
                "$recipe_snapshot_id",
                query.RecipeSnapshotId));
        }

        if (query.DeviceId is not null)
        {
            filters.Add(new QueryFilter("device_id = $device_id", "$device_id", query.DeviceId));
        }

        if (query.Judgement is not null)
        {
            filters.Add(new QueryFilter("judgement = $judgement", "$judgement", query.Judgement));
        }

        if (query.CompletedFromUtc is not null)
        {
            filters.Add(new QueryFilter(
                "completed_at_utc >= $completed_from_utc",
                "$completed_from_utc",
                FormatTimestamp(query.CompletedFromUtc.Value)));
        }

        if (query.CompletedToUtc is not null)
        {
            filters.Add(new QueryFilter(
                "completed_at_utc <= $completed_to_utc",
                "$completed_to_utc",
                FormatTimestamp(query.CompletedToUtc.Value)));
        }

        return filters;
    }

    private static void AddQueryParameters(SqliteCommand command, IEnumerable<QueryFilter> filters)
    {
        foreach (var filter in filters)
        {
            command.Parameters.AddWithValue(filter.ParameterName, filter.Value);
        }
    }

    private static void AddOptionalParameter(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }

    private static TraceRecord DeserializeTraceRecord(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedTraceRecord>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted trace record document is empty.");

        return TraceRecordSnapshotMapper.ToAggregate(snapshot);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }

    private sealed record QueryFilter(string Sql, string ParameterName, object Value);
}
