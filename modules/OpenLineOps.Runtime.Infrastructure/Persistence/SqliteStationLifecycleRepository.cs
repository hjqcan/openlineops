using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteStationLifecycleRepository :
    IStationLifecycleRepository,
    IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationLifecycleRepository(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<bool> TryAddAsync(
        StationLifecycle station,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO station_lifecycles (
                station_id,
                document_json,
                revision,
                mode,
                state,
                last_changed_at_utc,
                updated_at_utc)
            VALUES (
                $station_id,
                $document_json,
                0,
                $mode,
                $state,
                $last_changed_at_utc,
                $updated_at_utc)
            ON CONFLICT(station_id) DO NOTHING;
            """;
        AddParameters(command, station);
        var added = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
        if (added)
        {
            station.ClearDomainEvents();
        }

        return added;
    }

    public async ValueTask<long> SaveAsync(
        StationLifecycle station,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var nextRevision = checked(expectedRevision + 1);
        command.CommandText = """
            UPDATE station_lifecycles
            SET document_json = $document_json,
                revision = $next_revision,
                mode = $mode,
                state = $state,
                last_changed_at_utc = $last_changed_at_utc,
                updated_at_utc = $updated_at_utc
            WHERE station_id = $station_id
              AND revision = $expected_revision;
            """;
        AddParameters(command, station);
        command.Parameters.AddWithValue("$expected_revision", expectedRevision);
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new StationLifecycleConcurrencyException(
                station.Id,
                expectedRevision);
        }

        station.ClearDomainEvents();
        return nextRevision;
    }

    public async ValueTask<StationLifecyclePersistenceEntry?> GetByIdAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM station_lifecycles
            WHERE station_id = $station_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$station_id", stationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var snapshot = StationLifecyclePersistenceJson.Deserialize(reader.GetString(0));
        if (!string.Equals(
                snapshot.StationId.Value,
                stationId.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted Station lifecycle identity does not match key {stationId}.");
        }

        return new StationLifecyclePersistenceEntry(
            StationLifecycle.Restore(snapshot),
            reader.GetInt64(1));
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
                CREATE TABLE IF NOT EXISTS station_lifecycles (
                    station_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision >= 0),
                    mode TEXT NOT NULL,
                    state TEXT NOT NULL,
                    last_changed_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_station_lifecycles_state
                    ON station_lifecycles(state, station_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static void AddParameters(SqliteCommand command, StationLifecycle station)
    {
        command.Parameters.AddWithValue("$station_id", station.Id.Value);
        command.Parameters.AddWithValue(
            "$document_json",
            StationLifecyclePersistenceJson.Serialize(station.ToSnapshot()));
        command.Parameters.AddWithValue("$mode", station.Mode.ToString());
        command.Parameters.AddWithValue("$state", station.State.ToString());
        command.Parameters.AddWithValue(
            "$last_changed_at_utc",
            FormatTimestamp(station.LastChangedAtUtc));
        command.Parameters.AddWithValue(
            "$updated_at_utc",
            FormatTimestamp(DateTimeOffset.UtcNow));
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private static string RequireFileBackedConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "SQLite connection string is required.",
                nameof(connectionString));
        }

        var normalized = connectionString.Trim();
        var builder = new SqliteConnectionStringBuilder(normalized);
        if (builder.Mode == SqliteOpenMode.Memory
            || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Station lifecycle SQLite persistence requires a file-backed database; "
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

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
