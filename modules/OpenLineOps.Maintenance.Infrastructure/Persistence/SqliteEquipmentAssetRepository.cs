using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Maintenance.Application.Persistence;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Domain.Identifiers;

namespace OpenLineOps.Maintenance.Infrastructure.Persistence;

public sealed class SqliteEquipmentAssetRepository :
    IEquipmentAssetRepository,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteEquipmentAssetRepository(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<EquipmentAssetAddResult> TryAddAsync(
        EquipmentAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var facts = asset.Facts.ToArray();
        ValidateAggregate(asset, facts);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await InsertAssetAsync(
                    connection,
                    transaction,
                    asset,
                    facts.LongLength,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var fact in facts)
            {
                await InsertFactAsync(
                        connection,
                        transaction,
                        asset.Id.Value,
                        fact,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return EquipmentAssetAddResult.Added;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return EquipmentAssetAddResult.AlreadyExists;
        }
    }

    public async ValueTask<EquipmentAssetPersistenceEntry?> GetByIdAsync(
        EquipmentAssetId assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await LoadAsync(
                connection,
                transaction: null,
                assetId.Value,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<EquipmentAssetPersistenceEntry>>
        ListByStationAsync(
            string stationId,
            CancellationToken cancellationToken = default)
    {
        RequireCanonical(stationId, nameof(stationId), 96);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var assetIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT asset_id
                FROM maintenance_assets
                WHERE station_id = $station_id
                ORDER BY asset_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$station_id", stationId);
            await using var reader = await command.ExecuteReaderAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                assetIds.Add(reader.GetString(0));
            }
        }

        var entries = new List<EquipmentAssetPersistenceEntry>(assetIds.Count);
        foreach (var assetId in assetIds)
        {
            var entry = await LoadAsync(
                    connection,
                    transaction,
                    assetId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Equipment asset {assetId} disappeared from a read transaction.");
            entries.Add(entry);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entries;
    }

    public async ValueTask<long> SaveAsync(
        EquipmentAsset asset,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        var facts = asset.Facts.ToArray();
        ValidateAggregate(asset, facts);
        if (facts.LongLength < expectedRevision)
        {
            throw new InvalidDataException(
                $"Equipment asset {asset.Id} lost previously stored facts.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var storedRevision = await GetRevisionAsync(
                connection,
                transaction,
                asset.Id.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (storedRevision != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new EquipmentAssetConcurrencyException(
                asset.Id,
                expectedRevision);
        }

        await ValidateStoredPrefixAsync(
                connection,
                transaction,
                asset.Id.Value,
                facts,
                expectedRevision,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var fact in facts.Where(fact => fact.Sequence > expectedRevision))
        {
            await InsertFactAsync(
                    connection,
                    transaction,
                    asset.Id.Value,
                    fact,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var revision = facts.LongLength;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE maintenance_assets
                SET revision = $revision
                WHERE asset_id = $asset_id
                  AND revision = $expected_revision;
                """;
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$asset_id", asset.Id.Value);
            command.Parameters.AddWithValue(
                "$expected_revision",
                expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken)
                    .ConfigureAwait(false);
                throw new EquipmentAssetConcurrencyException(
                    asset.Id,
                    expectedRevision);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }

    private static async ValueTask<EquipmentAssetPersistenceEntry?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string assetId,
        CancellationToken cancellationToken)
    {
        string stationId;
        long revision;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT station_id, revision
                FROM maintenance_assets
                WHERE asset_id = $asset_id;
                """;
            command.Parameters.AddWithValue("$asset_id", assetId);
            await using var reader = await command.ExecuteReaderAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            stationId = reader.GetString(0);
            revision = reader.GetInt64(1);
        }

        var facts = new List<EquipmentFact>(checked((int)revision));
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence, fact_id, command_id, occurred_at_utc,
                       actor_id, kind, document_sha256, document_json
                FROM maintenance_facts
                WHERE asset_id = $asset_id
                ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$asset_id", assetId);
            await using var reader = await command.ExecuteReaderAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sequence = reader.GetInt64(0);
                var factId = reader.GetString(1);
                var commandId = reader.GetString(2);
                var occurredAtUtc = ParseUtc(reader.GetString(3));
                var actorId = reader.GetString(4);
                var kind = reader.GetString(5);
                var storedHash = reader.GetString(6);
                var document = reader.GetString(7);
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(storedHash),
                        Encoding.ASCII.GetBytes(ComputeHash(document))))
                {
                    throw new InvalidDataException(
                        $"Maintenance fact {factId} failed its integrity check.");
                }

                var fact = JsonSerializer.Deserialize<EquipmentFact>(
                        document,
                        JsonOptions)
                    ?? throw new InvalidDataException(
                        $"Maintenance fact {factId} has an empty document.");
                if (fact.Sequence != sequence
                    || !string.Equals(
                        fact.FactId,
                        factId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        fact.CommandId,
                        commandId,
                        StringComparison.Ordinal)
                    || fact.OccurredAtUtc != occurredAtUtc
                    || !string.Equals(
                        fact.ActorId,
                        actorId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        GetKind(fact),
                        kind,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Maintenance fact {factId} columns do not match its document.");
                }

                facts.Add(fact);
            }
        }

        if (revision != facts.Count)
        {
            throw new InvalidDataException(
                $"Equipment asset {assetId} revision does not match its fact stream.");
        }

        var asset = EquipmentAsset.Restore(facts);
        if (!string.Equals(asset.Id.Value, assetId, StringComparison.Ordinal)
            || !string.Equals(asset.StationId, stationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Equipment asset {assetId} header does not match its fact stream.");
        }

        return new EquipmentAssetPersistenceEntry(asset, revision);
    }

    private async ValueTask EnsureSchemaAsync(
        CancellationToken cancellationToken)
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

            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS maintenance_assets (
                    asset_id TEXT NOT NULL PRIMARY KEY,
                    station_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0)
                );

                CREATE INDEX IF NOT EXISTS ix_maintenance_assets_station
                    ON maintenance_assets(station_id, asset_id);

                CREATE TABLE IF NOT EXISTS maintenance_facts (
                    asset_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL CHECK(sequence > 0),
                    fact_id TEXT NOT NULL UNIQUE,
                    command_id TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    document_sha256 TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    PRIMARY KEY(asset_id, sequence),
                    UNIQUE(asset_id, command_id),
                    FOREIGN KEY(asset_id)
                        REFERENCES maintenance_assets(asset_id)
                        ON DELETE RESTRICT
                );

                CREATE TRIGGER IF NOT EXISTS maintenance_facts_no_update
                BEFORE UPDATE ON maintenance_facts
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'maintenance facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS maintenance_facts_no_delete
                BEFORE DELETE ON maintenance_facts
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'maintenance facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS maintenance_assets_no_delete
                BEFORE DELETE ON maintenance_assets
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'maintenance assets retain their fact history');
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
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

    private static async ValueTask InsertAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EquipmentAsset asset,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO maintenance_assets(asset_id, station_id, revision)
            VALUES($asset_id, $station_id, $revision);
            """;
        command.Parameters.AddWithValue("$asset_id", asset.Id.Value);
        command.Parameters.AddWithValue("$station_id", asset.StationId);
        command.Parameters.AddWithValue("$revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string assetId,
        EquipmentFact fact,
        CancellationToken cancellationToken)
    {
        var document = JsonSerializer.Serialize<EquipmentFact>(fact, JsonOptions);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO maintenance_facts(
                asset_id, sequence, fact_id, command_id, occurred_at_utc,
                actor_id, kind, document_sha256, document_json)
            VALUES(
                $asset_id, $sequence, $fact_id, $command_id, $occurred_at_utc,
                $actor_id, $kind, $document_sha256, $document_json);
            """;
        command.Parameters.AddWithValue("$asset_id", assetId);
        command.Parameters.AddWithValue("$sequence", fact.Sequence);
        command.Parameters.AddWithValue("$fact_id", fact.FactId);
        command.Parameters.AddWithValue("$command_id", fact.CommandId);
        command.Parameters.AddWithValue(
            "$occurred_at_utc",
            FormatUtc(fact.OccurredAtUtc));
        command.Parameters.AddWithValue("$actor_id", fact.ActorId);
        command.Parameters.AddWithValue("$kind", GetKind(fact));
        command.Parameters.AddWithValue(
            "$document_sha256",
            ComputeHash(document));
        command.Parameters.AddWithValue("$document_json", document);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long?> GetRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string assetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM maintenance_assets
            WHERE asset_id = $asset_id;
            """;
        command.Parameters.AddWithValue("$asset_id", assetId);
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask ValidateStoredPrefixAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string assetId,
        EquipmentFact[] facts,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, document_sha256
            FROM maintenance_facts
            WHERE asset_id = $asset_id
              AND sequence <= $expected_revision
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$asset_id", assetId);
        command.Parameters.AddWithValue("$expected_revision", expectedRevision);
        var index = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sequence = reader.GetInt64(0);
            if (sequence != index + 1L
                || index >= facts.Length
                || !string.Equals(
                    reader.GetString(1),
                    ComputeHash(
                        JsonSerializer.Serialize<EquipmentFact>(
                            facts[index],
                            JsonOptions)),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Equipment asset {assetId} does not preserve its stored fact prefix.");
            }

            index++;
        }

        if (index != expectedRevision)
        {
            throw new InvalidDataException(
                $"Equipment asset {assetId} stored fact prefix is incomplete.");
        }
    }

    private static void ValidateAggregate(
        EquipmentAsset asset,
        EquipmentFact[] facts)
    {
        if (facts.Length == 0
            || facts[0] is not EquipmentAssetRegisteredFact registration
            || !string.Equals(
                registration.AssetId,
                asset.Id.Value,
                StringComparison.Ordinal)
            || !string.Equals(
                registration.StationId,
                asset.StationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Equipment aggregate header does not match its registration fact.");
        }

        _ = EquipmentAsset.Restore(facts);
    }

    private static string GetKind(EquipmentFact fact) =>
        fact switch
        {
            EquipmentAssetRegisteredFact => "asset-registered",
            MaintenancePlanAddedFact => "plan-added",
            EquipmentUsageRecordedFact => "usage-recorded",
            MaintenanceEvaluationRecordedFact => "maintenance-evaluated",
            MaintenanceTaskRaisedFact => "task-raised",
            MaintenanceTaskCompletedFact => "task-completed",
            CalibrationStatusRecordedFact => "calibration-recorded",
            EquipmentHealthRecordedFact => "health-recorded",
            _ => throw new InvalidDataException(
                $"Unsupported equipment fact type {fact.GetType().Name}.")
        };

    private static string RequireFileBackedConnectionString(
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.Mode == SqliteOpenMode.Memory
            || string.Equals(
                builder.DataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            || builder.DataSource.StartsWith(
                "file:",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Maintenance SQLite persistence requires a file-backed database path.",
                nameof(connectionString));
        }

        var fullPath = Path.GetFullPath(builder.DataSource);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "Maintenance SQLite path has no parent directory.",
                nameof(connectionString));
        Directory.CreateDirectory(directory);
        builder.DataSource = fullPath;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        builder.Pooling = false;
        return builder.ToString();
    }

    private static string RequireCanonical(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName);
        }

        return value;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                namingPolicy: null,
                allowIntegerValues: false));
        return options;
    }

    private static string ComputeHash(string document) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(document)))
            .ToLowerInvariant();

    private static string FormatUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Maintenance fact timestamp must use the UTC offset.");
        }

        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"Persisted maintenance timestamp '{value}' is not canonical UTC.");
        }

        return parsed;
    }
}
