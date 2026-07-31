using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteStationLifecycleRepository :
    IStationLifecycleRepository,
    IStationLifecycleFactReader,
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
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var document = StationLifecyclePersistenceJson.Serialize(
            station.ToSnapshot());
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
        AddParameters(command, station, document);
        var added = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
        if (!added)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var fact = StationLifecycleFactIntegrity.Create(
            station.Id,
            sequence: 1,
            lifecycleRevision: 0,
            document,
            station,
            StationLifecycleFactIntegrity.GenesisSha256);
        await InsertFactAsync(
                connection,
                transaction,
                fact,
                document,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        station.ClearDomainEvents();
        return true;
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
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var nextRevision = checked(expectedRevision + 1);
        var document = StationLifecyclePersistenceJson.Serialize(
            station.ToSnapshot());
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
        AddParameters(command, station, document);
        command.Parameters.AddWithValue("$expected_revision", expectedRevision);
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new StationLifecycleConcurrencyException(
                station.Id,
                expectedRevision);
        }

        var previousFactSha256 = await GetFactSha256Async(
                connection,
                transaction,
                station.Id,
                checked(expectedRevision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (previousFactSha256 is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException(
                $"Station lifecycle {station.Id} is missing fact sequence "
                + $"{expectedRevision + 1}.");
        }

        var fact = StationLifecycleFactIntegrity.Create(
            station.Id,
            checked(nextRevision + 1),
            nextRevision,
            document,
            station,
            previousFactSha256);
        await InsertFactAsync(
                connection,
                transaction,
                fact,
                document,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<IReadOnlyList<StationLifecyclePersistenceEntry>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT station_id, document_json, revision
            FROM station_lifecycles
            ORDER BY station_id;
            """;
        var entries = new List<StationLifecyclePersistenceEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var stationId = new StationId(reader.GetString(0));
            var snapshot = StationLifecyclePersistenceJson.Deserialize(
                reader.GetString(1));
            if (snapshot.StationId != stationId)
            {
                throw new InvalidDataException(
                    $"Persisted Station lifecycle identity does not match key "
                    + $"{stationId}.");
            }

            entries.Add(new StationLifecyclePersistenceEntry(
                StationLifecycle.Restore(snapshot),
                reader.GetInt64(2)));
        }

        return entries.AsReadOnly();
    }

    public async ValueTask<IReadOnlyList<StationLifecycleFactMetadata>> ListFactsAsync(
        StationId stationId,
        long afterSequence = 0,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence,
                   lifecycle_revision,
                   kind,
                   document_json,
                   occurred_at_utc,
                   payload_sha256,
                   previous_fact_sha256,
                   fact_sha256
            FROM station_lifecycle_facts
            WHERE station_id = $station_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$station_id", stationId.Value);
        var allFacts = new List<StationLifecycleFactMetadata>();
        var expectedSequence = 1L;
        var previousFactSha256 = StationLifecycleFactIntegrity.GenesisSha256;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var document = reader.GetString(3);
            var fact = new StationLifecycleFactMetadata(
                stationId,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                ParseTimestamp(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7));
            if (fact.LifecycleRevision != fact.Sequence - 1)
            {
                throw new InvalidDataException(
                    $"Station lifecycle fact {stationId}/{fact.Sequence} has "
                    + "a non-contiguous lifecycle revision.");
            }

            StationLifecycleFactIntegrity.Validate(
                fact,
                document,
                previousFactSha256,
                expectedSequence);
            allFacts.Add(fact);
            previousFactSha256 = fact.FactSha256;
            expectedSequence++;
        }

        return allFacts
            .Where(fact => fact.Sequence > afterSequence)
            .Take(pageSize)
            .ToArray();
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

                CREATE TABLE IF NOT EXISTS station_lifecycle_facts (
                    station_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL CHECK(sequence >= 1),
                    lifecycle_revision INTEGER NOT NULL
                        CHECK(lifecycle_revision >= 0),
                    kind TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
                    previous_fact_sha256 TEXT NOT NULL
                        CHECK(length(previous_fact_sha256) = 64),
                    fact_sha256 TEXT NOT NULL CHECK(length(fact_sha256) = 64),
                    PRIMARY KEY (station_id, sequence),
                    UNIQUE (station_id, lifecycle_revision)
                );

                CREATE TRIGGER IF NOT EXISTS station_lifecycle_facts_no_update
                BEFORE UPDATE ON station_lifecycle_facts
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'station lifecycle facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS station_lifecycle_facts_no_delete
                BEFORE DELETE ON station_lifecycle_facts
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'station lifecycle facts are append-only');
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask<string?> GetFactSha256Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationId stationId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fact_sha256
            FROM station_lifecycle_facts
            WHERE station_id = $station_id
              AND sequence = $sequence
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$station_id", stationId.Value);
        command.Parameters.AddWithValue("$sequence", sequence);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask InsertFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationLifecycleFactMetadata fact,
        string document,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_lifecycle_facts (
                station_id,
                sequence,
                lifecycle_revision,
                kind,
                document_json,
                occurred_at_utc,
                payload_sha256,
                previous_fact_sha256,
                fact_sha256)
            VALUES (
                $station_id,
                $sequence,
                $lifecycle_revision,
                $kind,
                $document_json,
                $occurred_at_utc,
                $payload_sha256,
                $previous_fact_sha256,
                $fact_sha256);
            """;
        command.Parameters.AddWithValue("$station_id", fact.StationId.Value);
        command.Parameters.AddWithValue("$sequence", fact.Sequence);
        command.Parameters.AddWithValue(
            "$lifecycle_revision",
            fact.LifecycleRevision);
        command.Parameters.AddWithValue("$kind", fact.Kind);
        command.Parameters.AddWithValue("$document_json", document);
        command.Parameters.AddWithValue(
            "$occurred_at_utc",
            FormatTimestamp(fact.OccurredAtUtc));
        command.Parameters.AddWithValue("$payload_sha256", fact.PayloadSha256);
        command.Parameters.AddWithValue(
            "$previous_fact_sha256",
            fact.PreviousFactSha256);
        command.Parameters.AddWithValue("$fact_sha256", fact.FactSha256);
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddParameters(
        SqliteCommand command,
        StationLifecycle station,
        string document)
    {
        command.Parameters.AddWithValue("$station_id", station.Id.Value);
        command.Parameters.AddWithValue("$document_json", document);
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

    private static DateTimeOffset ParseTimestamp(string value)
    {
        var parsed = DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        if (parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Station lifecycle fact timestamp must be UTC.");
        }

        return parsed;
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
