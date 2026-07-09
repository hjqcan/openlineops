using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Infrastructure.Persistence;

public sealed class PostgresTraceRecordRepository :
    ITraceRecordRepository,
    IDisposable,
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public PostgresTraceRecordRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString.Trim());
    }

    public async ValueTask SaveAsync(
        TraceRecord traceRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceRecord);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = TraceRecordSnapshotMapper.ToSnapshot(traceRecord);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trace_records (
                trace_id,
                document_json,
                runtime_session_id,
                project_id,
                application_id,
                project_snapshot_id,
                topology_id,
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
                @trace_id,
                @document_json::jsonb,
                @runtime_session_id,
                @project_id,
                @application_id,
                @project_snapshot_id,
                @topology_id,
                @serial_number,
                @batch_id,
                @station_id,
                @fixture_id,
                @process_definition_id,
                @process_version_id,
                @configuration_snapshot_id,
                @recipe_snapshot_id,
                @device_id,
                @judgement,
                @started_at_utc,
                @completed_at_utc,
                @recorded_by,
                @updated_at_utc)
            ON CONFLICT (trace_id) DO UPDATE SET
                document_json = EXCLUDED.document_json,
                runtime_session_id = EXCLUDED.runtime_session_id,
                project_id = EXCLUDED.project_id,
                application_id = EXCLUDED.application_id,
                project_snapshot_id = EXCLUDED.project_snapshot_id,
                topology_id = EXCLUDED.topology_id,
                serial_number = EXCLUDED.serial_number,
                batch_id = EXCLUDED.batch_id,
                station_id = EXCLUDED.station_id,
                fixture_id = EXCLUDED.fixture_id,
                process_definition_id = EXCLUDED.process_definition_id,
                process_version_id = EXCLUDED.process_version_id,
                configuration_snapshot_id = EXCLUDED.configuration_snapshot_id,
                recipe_snapshot_id = EXCLUDED.recipe_snapshot_id,
                device_id = EXCLUDED.device_id,
                judgement = EXCLUDED.judgement,
                started_at_utc = EXCLUDED.started_at_utc,
                completed_at_utc = EXCLUDED.completed_at_utc,
                recorded_by = EXCLUDED.recorded_by,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """;
        command.Parameters.AddWithValue("trace_id", traceRecord.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("document_json", documentJson);
        command.Parameters.AddWithValue("runtime_session_id", traceRecord.RuntimeSessionId.Value.ToString("D"));
        AddOptionalParameter(command, "project_id", traceRecord.ProjectId);
        AddOptionalParameter(command, "application_id", traceRecord.ApplicationId);
        AddOptionalParameter(command, "project_snapshot_id", traceRecord.ProjectSnapshotId);
        AddOptionalParameter(command, "topology_id", traceRecord.TopologyId);
        command.Parameters.AddWithValue("serial_number", traceRecord.SerialNumber);
        AddOptionalParameter(command, "batch_id", traceRecord.BatchId);
        command.Parameters.AddWithValue("station_id", traceRecord.StationId.Value);
        AddOptionalParameter(command, "fixture_id", traceRecord.FixtureId);
        command.Parameters.AddWithValue("process_definition_id", traceRecord.ProcessDefinitionId.Value);
        command.Parameters.AddWithValue("process_version_id", traceRecord.ProcessVersionId.Value);
        command.Parameters.AddWithValue("configuration_snapshot_id", traceRecord.ConfigurationSnapshotId.Value);
        command.Parameters.AddWithValue("recipe_snapshot_id", traceRecord.RecipeSnapshotId.Value);
        command.Parameters.AddWithValue("device_id", traceRecord.DeviceId.Value);
        command.Parameters.AddWithValue("judgement", traceRecord.Judgement.ToString());
        command.Parameters.AddWithValue("started_at_utc", traceRecord.StartedAtUtc);
        command.Parameters.AddWithValue("completed_at_utc", traceRecord.CompletedAtUtc);
        command.Parameters.AddWithValue("recorded_by", traceRecord.RecordedBy.Value);
        command.Parameters.AddWithValue("updated_at_utc", DateTimeOffset.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TraceRecord?> GetByIdAsync(
        TraceRecordId traceRecordId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text
            FROM trace_records
            WHERE trace_id = @trace_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("trace_id", traceRecordId.Value.ToString("D"));

        var documentJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return documentJson is null
            ? null
            : DeserializeTraceRecord((string)documentJson);
    }

    public async ValueTask<PagedResult<TraceRecord>> QueryAsync(
        TraceRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var paging = query.Paging.Normalize(TraceRecordQuery.MaxPageSize);
        var filters = BuildFilters(query);
        var whereClause = filters.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", filters.Select(filter => filter.Sql));

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var totalCount = await CountAsync(connection, whereClause, filters, cancellationToken)
            .ConfigureAwait(false);
        var records = await QueryPageAsync(connection, whereClause, filters, paging, cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<TraceRecord>(
            records,
            paging.PageNumber,
            paging.PageSize,
            totalCount);
    }

    private static async ValueTask<long> CountAsync(
        NpgsqlConnection connection,
        string whereClause,
        IReadOnlyCollection<QueryFilter> filters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM trace_records {whereClause};";
        AddQueryParameters(command, filters);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<IReadOnlyCollection<TraceRecord>> QueryPageAsync(
        NpgsqlConnection connection,
        string whereClause,
        IReadOnlyCollection<QueryFilter> filters,
        PagedRequest paging,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT document_json::text
            FROM trace_records
            {whereClause}
            ORDER BY completed_at_utc, trace_id
            LIMIT @take OFFSET @skip;
            """;
        AddQueryParameters(command, filters);
        command.Parameters.AddWithValue("take", paging.PageSize);
        command.Parameters.AddWithValue("skip", paging.Skip);

        var records = new List<TraceRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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

        await _schemaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            await using var connection = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS trace_records (
                    trace_id text NOT NULL PRIMARY KEY,
                    document_json jsonb NOT NULL,
                    runtime_session_id text NOT NULL,
                    project_id text NULL,
                    application_id text NULL,
                    project_snapshot_id text NULL,
                    topology_id text NULL,
                    serial_number text NOT NULL,
                    batch_id text NULL,
                    station_id text NOT NULL,
                    fixture_id text NULL,
                    process_definition_id text NOT NULL,
                    process_version_id text NOT NULL,
                    configuration_snapshot_id text NOT NULL,
                    recipe_snapshot_id text NOT NULL,
                    device_id text NOT NULL,
                    judgement text NOT NULL,
                    started_at_utc timestamptz NOT NULL,
                    completed_at_utc timestamptz NOT NULL,
                    recorded_by text NOT NULL,
                    updated_at_utc timestamptz NOT NULL
                );

                ALTER TABLE trace_records ADD COLUMN IF NOT EXISTS project_id text NULL;
                ALTER TABLE trace_records ADD COLUMN IF NOT EXISTS application_id text NULL;
                ALTER TABLE trace_records ADD COLUMN IF NOT EXISTS project_snapshot_id text NULL;
                ALTER TABLE trace_records ADD COLUMN IF NOT EXISTS topology_id text NULL;

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

                CREATE INDEX IF NOT EXISTS ix_trace_records_project_completed
                    ON trace_records(project_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_project_snapshot_completed
                    ON trace_records(project_snapshot_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_process_completed
                    ON trace_records(process_version_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_device_completed
                    ON trace_records(device_id, completed_at_utc, trace_id);

                CREATE INDEX IF NOT EXISTS ix_trace_records_judgement_completed
                    ON trace_records(judgement, completed_at_utc, trace_id);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static List<QueryFilter> BuildFilters(TraceRecordQuery query)
    {
        var filters = new List<QueryFilter>();

        if (query.SerialNumber is not null)
        {
            filters.Add(new QueryFilter("serial_number = @serial_number", "serial_number", query.SerialNumber));
        }

        if (query.BatchId is not null)
        {
            filters.Add(new QueryFilter("batch_id = @batch_id", "batch_id", query.BatchId));
        }

        if (query.StationId is not null)
        {
            filters.Add(new QueryFilter("station_id = @station_id", "station_id", query.StationId));
        }

        if (query.FixtureId is not null)
        {
            filters.Add(new QueryFilter("fixture_id = @fixture_id", "fixture_id", query.FixtureId));
        }

        if (query.ProcessDefinitionId is not null)
        {
            filters.Add(new QueryFilter(
                "process_definition_id = @process_definition_id",
                "process_definition_id",
                query.ProcessDefinitionId));
        }

        if (query.ProcessVersionId is not null)
        {
            filters.Add(new QueryFilter(
                "process_version_id = @process_version_id",
                "process_version_id",
                query.ProcessVersionId));
        }

        if (query.ConfigurationSnapshotId is not null)
        {
            filters.Add(new QueryFilter(
                "configuration_snapshot_id = @configuration_snapshot_id",
                "configuration_snapshot_id",
                query.ConfigurationSnapshotId));
        }

        if (query.RecipeSnapshotId is not null)
        {
            filters.Add(new QueryFilter(
                "recipe_snapshot_id = @recipe_snapshot_id",
                "recipe_snapshot_id",
                query.RecipeSnapshotId));
        }

        if (query.DeviceId is not null)
        {
            filters.Add(new QueryFilter("device_id = @device_id", "device_id", query.DeviceId));
        }

        if (query.Judgement is not null)
        {
            filters.Add(new QueryFilter("judgement = @judgement", "judgement", query.Judgement));
        }

        if (query.ProjectId is not null)
        {
            filters.Add(new QueryFilter("project_id = @project_id", "project_id", query.ProjectId));
        }

        if (query.ApplicationId is not null)
        {
            filters.Add(new QueryFilter("application_id = @application_id", "application_id", query.ApplicationId));
        }

        if (query.ProjectSnapshotId is not null)
        {
            filters.Add(new QueryFilter(
                "project_snapshot_id = @project_snapshot_id",
                "project_snapshot_id",
                query.ProjectSnapshotId));
        }

        if (query.TopologyId is not null)
        {
            filters.Add(new QueryFilter("topology_id = @topology_id", "topology_id", query.TopologyId));
        }

        if (query.CompletedFromUtc is not null)
        {
            filters.Add(new QueryFilter(
                "completed_at_utc >= @completed_from_utc",
                "completed_from_utc",
                query.CompletedFromUtc.Value));
        }

        if (query.CompletedToUtc is not null)
        {
            filters.Add(new QueryFilter(
                "completed_at_utc <= @completed_to_utc",
                "completed_to_utc",
                query.CompletedToUtc.Value));
        }

        return filters;
    }

    private static void AddQueryParameters(NpgsqlCommand command, IEnumerable<QueryFilter> filters)
    {
        foreach (var filter in filters)
        {
            command.Parameters.AddWithValue(filter.ParameterName, filter.Value);
        }
    }

    private static void AddOptionalParameter(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }

    private static TraceRecord DeserializeTraceRecord(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedTraceRecord>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted trace record document is empty.");

        return TraceRecordSnapshotMapper.ToAggregate(snapshot);
    }

    public void Dispose()
    {
        _dataSource.Dispose();
        _schemaLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        _schemaLock.Dispose();
    }

    private sealed record QueryFilter(string Sql, string ParameterName, object Value);
}
