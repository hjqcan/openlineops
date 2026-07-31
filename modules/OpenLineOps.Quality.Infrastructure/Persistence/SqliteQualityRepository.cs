using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Quality.Application.Persistence;

namespace OpenLineOps.Quality.Infrastructure.Persistence;

public sealed partial class SqliteQualityRepository : IQualityRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _schemaCreated;

    public SqliteQualityRepository(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<QualityIdempotencyCheck> CheckIdempotencyAsync(
        QualityIdempotencyContext idempotency,
        string resourceKind,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdempotency(idempotency, resourceKind, resourceId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await CheckIdempotencyAsync(
                connection,
                transaction: null,
                idempotency,
                resourceKind,
                resourceId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            EnsureDatabaseDirectory();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS quality_test_plan_revisions (
                    test_plan_revision_id TEXT NOT NULL PRIMARY KEY,
                    test_plan_id TEXT NOT NULL,
                    revision_number INTEGER NOT NULL CHECK(revision_number > 0),
                    created_at_utc TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    UNIQUE(test_plan_id, revision_number)
                );

                CREATE INDEX IF NOT EXISTS ix_quality_test_plan_revisions_plan
                    ON quality_test_plan_revisions(test_plan_id, revision_number);

                CREATE TABLE IF NOT EXISTS quality_calibration_assets (
                    calibration_asset_id TEXT NOT NULL PRIMARY KEY,
                    asset_code TEXT NOT NULL UNIQUE,
                    instrument_id TEXT NOT NULL,
                    valid_until_utc TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    document_json TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_quality_calibration_assets_instrument
                    ON quality_calibration_assets(instrument_id, valid_until_utc);

                CREATE TABLE IF NOT EXISTS quality_test_attempts (
                    test_attempt_id TEXT NOT NULL PRIMARY KEY,
                    test_plan_revision_id TEXT NOT NULL,
                    production_unit_id TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    attempt_number INTEGER NOT NULL CHECK(attempt_number > 0),
                    started_at_utc TEXT NOT NULL,
                    current_revision INTEGER NOT NULL CHECK(current_revision > 0),
                    status TEXT NOT NULL,
                    judgement TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    UNIQUE(production_unit_id, test_plan_revision_id, attempt_number),
                    FOREIGN KEY(test_plan_revision_id)
                        REFERENCES quality_test_plan_revisions(test_plan_revision_id)
                        ON DELETE RESTRICT
                );

                CREATE INDEX IF NOT EXISTS ix_quality_test_attempts_query
                    ON quality_test_attempts(station_id, production_unit_id, started_at_utc);

                CREATE TABLE IF NOT EXISTS quality_test_attempt_events (
                    test_attempt_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0),
                    event_kind TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    PRIMARY KEY(test_attempt_id, revision),
                    FOREIGN KEY(test_attempt_id)
                        REFERENCES quality_test_attempts(test_attempt_id)
                        ON DELETE RESTRICT
                );

                CREATE TABLE IF NOT EXISTS quality_measurement_results (
                    measurement_result_id TEXT NOT NULL PRIMARY KEY,
                    test_attempt_id TEXT NOT NULL,
                    attempt_revision INTEGER NOT NULL CHECK(attempt_revision > 1),
                    test_characteristic_id TEXT NOT NULL,
                    measured_at_utc TEXT NOT NULL,
                    judgement TEXT NOT NULL,
                    evidence_sha256 TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    UNIQUE(test_attempt_id, attempt_revision),
                    UNIQUE(test_attempt_id, test_characteristic_id),
                    FOREIGN KEY(test_attempt_id)
                        REFERENCES quality_test_attempts(test_attempt_id)
                        ON DELETE RESTRICT
                );

                CREATE INDEX IF NOT EXISTS ix_quality_measurements_attempt
                    ON quality_measurement_results(test_attempt_id, attempt_revision);

                CREATE TABLE IF NOT EXISTS quality_nonconformances (
                    nonconformance_id TEXT NOT NULL PRIMARY KEY,
                    test_attempt_id TEXT NOT NULL,
                    measurement_result_id TEXT NOT NULL UNIQUE,
                    production_unit_id TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    current_revision INTEGER NOT NULL CHECK(current_revision > 0),
                    status TEXT NOT NULL,
                    raised_at_utc TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    FOREIGN KEY(test_attempt_id)
                        REFERENCES quality_test_attempts(test_attempt_id)
                        ON DELETE RESTRICT,
                    FOREIGN KEY(measurement_result_id)
                        REFERENCES quality_measurement_results(measurement_result_id)
                        ON DELETE RESTRICT
                );

                CREATE INDEX IF NOT EXISTS ix_quality_nonconformances_query
                    ON quality_nonconformances(status, station_id, production_unit_id, raised_at_utc);

                CREATE TABLE IF NOT EXISTS quality_nonconformance_events (
                    nonconformance_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0),
                    event_kind TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    PRIMARY KEY(nonconformance_id, revision),
                    FOREIGN KEY(nonconformance_id)
                        REFERENCES quality_nonconformances(nonconformance_id)
                        ON DELETE RESTRICT
                );

                CREATE TABLE IF NOT EXISTS quality_idempotency (
                    scope TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL,
                    request_sha256 TEXT NOT NULL,
                    resource_kind TEXT NOT NULL,
                    resource_id TEXT NOT NULL,
                    result_revision INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    PRIMARY KEY(scope, idempotency_key)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async ValueTask<QualityIdempotencyCheck> CheckIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        QualityIdempotencyContext idempotency,
        string resourceKind,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_sha256, resource_kind, resource_id, result_revision
            FROM quality_idempotency
            WHERE scope = $scope AND idempotency_key = $idempotency_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$scope", idempotency.Scope);
        command.Parameters.AddWithValue("$idempotency_key", idempotency.Key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new QualityIdempotencyCheck(QualityIdempotencyStatus.Missing);
        }

        var matches = string.Equals(reader.GetString(0), idempotency.RequestSha256, StringComparison.Ordinal)
            && string.Equals(reader.GetString(1), resourceKind, StringComparison.Ordinal)
            && string.Equals(reader.GetString(2), resourceId.ToString("D"), StringComparison.Ordinal);
        return matches
            ? new QualityIdempotencyCheck(QualityIdempotencyStatus.Replay, reader.GetInt32(3))
            : new QualityIdempotencyCheck(QualityIdempotencyStatus.Conflict);
    }

    private static async ValueTask InsertIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QualityIdempotencyContext idempotency,
        string resourceKind,
        Guid resourceId,
        int resultRevision,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quality_idempotency (
                scope, idempotency_key, request_sha256, resource_kind,
                resource_id, result_revision, created_at_utc)
            VALUES (
                $scope, $idempotency_key, $request_sha256, $resource_kind,
                $resource_id, $result_revision, $created_at_utc);
            """;
        command.Parameters.AddWithValue("$scope", idempotency.Scope);
        command.Parameters.AddWithValue("$idempotency_key", idempotency.Key);
        command.Parameters.AddWithValue("$request_sha256", idempotency.RequestSha256);
        command.Parameters.AddWithValue("$resource_kind", resourceKind);
        command.Parameters.AddWithValue("$resource_id", resourceId.ToString("D"));
        command.Parameters.AddWithValue("$result_revision", resultRevision);
        command.Parameters.AddWithValue("$created_at_utc", FormatTimestamp(createdAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int?> GetCurrentRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string idColumn,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        var allowed = (tableName, idColumn) is
            ("quality_test_attempts", "test_attempt_id")
            or ("quality_nonconformances", "nonconformance_id");
        if (!allowed)
        {
            throw new ArgumentException("Unsupported optimistic concurrency target.", nameof(tableName));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT current_revision FROM {tableName} WHERE {idColumn} = $resource_id LIMIT 1;";
        command.Parameters.AddWithValue("$resource_id", resourceId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision
            ? checked((int)revision)
            : null;
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string RequireFileBackedConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        }

        var normalized = connectionString.Trim();
        var builder = new SqliteConnectionStringBuilder(normalized);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Quality SQLite persistence requires a file-backed database.",
                nameof(connectionString));
        }

        return normalized;
    }

    private static void ValidateIdempotency(
        QualityIdempotencyContext idempotency,
        string resourceKind,
        Guid resourceId)
    {
        ArgumentNullException.ThrowIfNull(idempotency);
        if (string.IsNullOrWhiteSpace(idempotency.Scope)
            || string.IsNullOrWhiteSpace(idempotency.Key)
            || idempotency.RequestSha256.Length != 64
            || !idempotency.RequestSha256.All(
                character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            || string.IsNullOrWhiteSpace(resourceKind)
            || resourceId == Guid.Empty)
        {
            throw new ArgumentException("Quality idempotency evidence is invalid.", nameof(idempotency));
        }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException($"Persisted Quality {typeof(T).Name} document is empty.");

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static TEnum ParseCanonicalEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
                ? parsed
                : throw new InvalidDataException(
                    $"Persisted Quality {fieldName} value '{value}' is invalid.");
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
        _gate.Dispose();
    }
}
