using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Operations.Metrics.Application.Persistence;

namespace OpenLineOps.Operations.Metrics.Infrastructure.Persistence;

public sealed partial class SqliteOperationsMetricsStore :
    IOperationsMetricsStore,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _schemaCreated;

    public SqliteOperationsMetricsStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(
                builder.DataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "Operations metrics persistence requires a file-backed SQLite data source.",
                nameof(connectionString));
        }

        var path = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new ArgumentException(
                "Operations metrics SQLite path has no parent directory.",
                nameof(connectionString)));
        builder.DataSource = path;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        _connectionString = builder.ToString();
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
        _schemaGate.Dispose();
        _writeGate.Dispose();
    }

    private async ValueTask EnsureSchemaAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS operations_metric_production_events (
                    event_id TEXT NOT NULL PRIMARY KEY,
                    station_id TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_operations_metric_events_query
                    ON operations_metric_production_events(
                        station_id, occurred_at_utc, received_at_utc, event_id);

                CREATE TABLE IF NOT EXISTS operations_metric_shift_definitions (
                    shift_id TEXT NOT NULL PRIMARY KEY,
                    station_id TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_operations_metric_shifts_station
                    ON operations_metric_shift_definitions(station_id, shift_id);

                CREATE TABLE IF NOT EXISTS operations_metric_planned_windows (
                    window_id TEXT NOT NULL PRIMARY KEY,
                    shift_id TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    starts_at_utc TEXT NOT NULL,
                    ends_at_utc TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    FOREIGN KEY(shift_id)
                        REFERENCES operations_metric_shift_definitions(shift_id)
                        ON DELETE RESTRICT
                );

                CREATE INDEX IF NOT EXISTS ix_operations_metric_windows_query
                    ON operations_metric_planned_windows(
                        station_id, starts_at_utc, ends_at_utc, window_id);

                CREATE TABLE IF NOT EXISTS operations_metric_downtime_facts (
                    fact_id TEXT NOT NULL PRIMARY KEY,
                    downtime_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0),
                    station_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    previous_sha256 TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    UNIQUE(downtime_id, revision)
                );

                CREATE INDEX IF NOT EXISTS ix_operations_metric_downtime_query
                    ON operations_metric_downtime_facts(
                        station_id, downtime_id, revision);

                CREATE TRIGGER IF NOT EXISTS operations_metric_events_no_update
                BEFORE UPDATE ON operations_metric_production_events
                BEGIN
                    SELECT RAISE(ABORT, 'production events are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS operations_metric_events_no_delete
                BEFORE DELETE ON operations_metric_production_events
                BEGIN
                    SELECT RAISE(ABORT, 'production events are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS operations_metric_shifts_no_update
                BEFORE UPDATE ON operations_metric_shift_definitions
                BEGIN
                    SELECT RAISE(ABORT, 'shift definitions are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS operations_metric_shifts_no_delete
                BEFORE DELETE ON operations_metric_shift_definitions
                BEGIN
                    SELECT RAISE(ABORT, 'shift definitions are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS operations_metric_windows_no_update
                BEFORE UPDATE ON operations_metric_planned_windows
                BEGIN
                    SELECT RAISE(ABORT, 'planned windows are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS operations_metric_windows_no_delete
                BEFORE DELETE ON operations_metric_planned_windows
                BEGIN
                    SELECT RAISE(ABORT, 'planned windows are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS operations_metric_downtime_no_update
                BEFORE UPDATE ON operations_metric_downtime_facts
                BEGIN
                    SELECT RAISE(ABORT, 'downtime facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS operations_metric_downtime_no_delete
                BEFORE DELETE ON operations_metric_downtime_facts
                BEGIN
                    SELECT RAISE(ABORT, 'downtime facts are append-only');
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        return connection;
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T DeserializeAndVerify<T>(
        string payload,
        string expectedSha256)
    {
        var actual = ComputeSha256(payload);
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Operations metrics persisted fact hash verification failed.");
        }

        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
               ?? throw new InvalidDataException(
                   $"Operations metrics persisted {typeof(T).Name} is invalid.");
    }

    private static string ComputeSha256(string payload)
    {
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string ComputeChainedSha256(
        string previousSha256,
        string payload) =>
        ComputeSha256(string.Concat(previousSha256, "\n", payload));

    private static string FormatUtc(DateTimeOffset value)
    {
        ValidateUtc(value);
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static void ValidateUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Operations metrics timestamps must be non-default UTC.");
        }
    }

    private static void ValidateQuery(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ValidateUtc(fromUtc);
        ValidateUtc(toUtc);
        if (!string.Equals(stationId, stationId.Trim(), StringComparison.Ordinal)
            || toUtc <= fromUtc)
        {
            throw new ArgumentException(
                "An operations metrics query requires a canonical station and a non-empty interval.");
        }
    }
}
