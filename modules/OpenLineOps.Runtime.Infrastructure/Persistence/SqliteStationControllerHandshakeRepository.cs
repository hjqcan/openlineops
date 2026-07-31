using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteStationControllerHandshakeRepository :
    IStationControllerHandshakeRepository,
    IStationControllerHandshakeFactReader,
    IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationControllerHandshakeRepository(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<StationControllerHandshakePersistenceEntry?> GetByIdAsync(
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
            FROM station_controller_handshakes
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

        var snapshot = StationControllerHandshakePersistenceJson.DeserializeSnapshot(
            reader.GetString(0));
        if (snapshot.StationId != stationId)
        {
            throw new InvalidDataException(
                $"Persisted Station controller handshake identity does not match "
                + $"key {stationId}.");
        }

        return new StationControllerHandshakePersistenceEntry(
            StationControllerHandshake.Restore(snapshot),
            reader.GetInt64(1));
    }

    public async ValueTask<IReadOnlyList<StationControllerHandshakeFact>> ListFactsAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        return (await ReadAllFactRecordsAsync(stationId, cancellationToken)
                .ConfigureAwait(false))
            .Select(static record => record.Fact)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<StationControllerHandshakeFactRecord>>
        ListFactRecordsAsync(
            StationId stationId,
            long afterSequence = 0,
            int pageSize = 100,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        return (await ReadAllFactRecordsAsync(stationId, cancellationToken)
                .ConfigureAwait(false))
            .Where(record => record.Fact.Sequence > afterSequence)
            .Take(pageSize)
            .ToArray();
    }

    public async ValueTask<bool> TryAddAsync(
        StationControllerHandshake state,
        StationControllerHandshakeFact initialFact,
        CancellationToken cancellationToken = default)
    {
        ValidateFact(state, initialFact, expectedSequence: 1);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_controller_handshakes (
                station_id,
                document_json,
                revision,
                controller_session_id,
                heartbeat_sequence,
                received_at_utc,
                updated_at_utc)
            VALUES (
                $station_id,
                $document_json,
                0,
                $controller_session_id,
                $heartbeat_sequence,
                $received_at_utc,
                $updated_at_utc)
            ON CONFLICT(station_id) DO NOTHING;
            """;
        AddStateParameters(command, state);
        var added = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
        if (!added)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await InsertFactAsync(
                connection,
                transaction,
                initialFact,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<long> SaveAsync(
        StationControllerHandshake state,
        long expectedRevision,
        StationControllerHandshakeFact? fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (fact is not null)
        {
            ValidateFact(state, fact, checked(expectedRevision + 2));
        }
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var nextRevision = checked(expectedRevision + 1);
        command.CommandText = """
            UPDATE station_controller_handshakes
            SET document_json = $document_json,
                revision = $next_revision,
                controller_session_id = $controller_session_id,
                heartbeat_sequence = $heartbeat_sequence,
                received_at_utc = $received_at_utc,
                updated_at_utc = $updated_at_utc
            WHERE station_id = $station_id
              AND revision = $expected_revision;
            """;
        AddStateParameters(command, state);
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        command.Parameters.AddWithValue("$expected_revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new StationControllerHandshakeConcurrencyException(
                state.StationId,
                expectedRevision);
        }

        if (fact is not null)
        {
            await InsertFactAsync(connection, transaction, fact, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return nextRevision;
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
                CREATE TABLE IF NOT EXISTS station_controller_handshakes (
                    station_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision >= 0),
                    controller_session_id TEXT NOT NULL,
                    heartbeat_sequence INTEGER NOT NULL CHECK(heartbeat_sequence >= 1),
                    received_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS station_controller_handshake_facts (
                    station_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL CHECK(sequence >= 1),
                    kind TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    PRIMARY KEY (station_id, sequence)
                );

                CREATE TABLE IF NOT EXISTS
                    station_controller_handshake_fact_integrity (
                        station_id TEXT NOT NULL,
                        sequence INTEGER NOT NULL CHECK(sequence >= 1),
                        payload_sha256 TEXT NOT NULL
                            CHECK(length(payload_sha256) = 64),
                        previous_fact_sha256 TEXT NOT NULL
                            CHECK(length(previous_fact_sha256) = 64),
                        fact_sha256 TEXT NOT NULL
                            CHECK(length(fact_sha256) = 64),
                        PRIMARY KEY (station_id, sequence)
                    );

                CREATE INDEX IF NOT EXISTS ix_station_controller_handshakes_received
                    ON station_controller_handshakes(received_at_utc, station_id);

                CREATE TRIGGER IF NOT EXISTS
                    station_controller_handshake_facts_no_update
                BEFORE UPDATE ON station_controller_handshake_facts
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'station controller handshake facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS
                    station_controller_handshake_facts_no_delete
                BEFORE DELETE ON station_controller_handshake_facts
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'station controller handshake facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS
                    station_controller_handshake_fact_integrity_no_update
                BEFORE UPDATE ON station_controller_handshake_fact_integrity
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'station controller handshake integrity is append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS
                    station_controller_handshake_fact_integrity_no_delete
                BEFORE DELETE ON station_controller_handshake_fact_integrity
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'station controller handshake integrity is append-only');
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await BackfillFactIntegrityAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask InsertFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationControllerHandshakeFact fact,
        CancellationToken cancellationToken)
    {
        var document = StationControllerHandshakePersistenceJson.SerializeFact(fact);
        var previousFactSha256 = await GetPreviousFactSha256Async(
                connection,
                transaction,
                fact.StationId.Value,
                fact.Sequence,
                cancellationToken)
            .ConfigureAwait(false)
            ?? StationControllerHandshakeFactIntegrity.GenesisSha256;
        var record = StationControllerHandshakeFactIntegrity.Create(
            fact,
            document,
            previousFactSha256);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_controller_handshake_facts (
                station_id,
                sequence,
                kind,
                document_json,
                occurred_at_utc)
            VALUES (
                $station_id,
                $sequence,
                $kind,
                $document_json,
                $occurred_at_utc);
            """;
        command.Parameters.AddWithValue("$station_id", fact.StationId.Value);
        command.Parameters.AddWithValue("$sequence", fact.Sequence);
        command.Parameters.AddWithValue("$kind", fact.Kind.ToString());
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue(
            "$occurred_at_utc",
            FormatTimestamp(fact.OccurredAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var integrityCommand = connection.CreateCommand();
        integrityCommand.Transaction = transaction;
        integrityCommand.CommandText = """
            INSERT INTO station_controller_handshake_fact_integrity (
                station_id,
                sequence,
                payload_sha256,
                previous_fact_sha256,
                fact_sha256)
            VALUES (
                $station_id,
                $sequence,
                $payload_sha256,
                $previous_fact_sha256,
                $fact_sha256);
            """;
        AddIntegrityParameters(integrityCommand, record);
        _ = await integrityCommand.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddStateParameters(
        SqliteCommand command,
        StationControllerHandshake state)
    {
        var report = state.LatestReport;
        command.Parameters.AddWithValue("$station_id", state.StationId.Value);
        command.Parameters.AddWithValue(
            "$document_json",
            StationControllerHandshakePersistenceJson.SerializeSnapshot(
                state.ToSnapshot()));
        command.Parameters.AddWithValue(
            "$controller_session_id",
            report.ControllerSessionId);
        command.Parameters.AddWithValue(
            "$heartbeat_sequence",
            report.HeartbeatSequence);
        command.Parameters.AddWithValue(
            "$received_at_utc",
            FormatTimestamp(report.ReceivedAtUtc));
        command.Parameters.AddWithValue(
            "$updated_at_utc",
            FormatTimestamp(state.LastChangedAtUtc));
    }

    private static void ValidateFact(
        StationControllerHandshake state,
        StationControllerHandshakeFact fact,
        long expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.StationId != state.StationId
            || fact.Sequence != expectedSequence)
        {
            throw new ArgumentException(
                "Controller handshake fact identity or sequence does not match "
                + "the state revision.",
                nameof(fact));
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

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
            || builder.DataSource.Contains(
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            || (builder.DataSource.StartsWith(
                    "file:",
                    StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains(
                    "mode=memory",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Station controller handshake SQLite persistence requires a "
                + "file-backed database; use the InMemory provider for transient execution.",
                nameof(connectionString));
        }

        return normalized;
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(
                dataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
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

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
