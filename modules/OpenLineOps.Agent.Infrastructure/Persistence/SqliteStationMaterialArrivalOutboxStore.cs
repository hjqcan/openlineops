using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class SqliteStationMaterialArrivalOutboxStore :
    IStationMaterialArrivalOutboxStore,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        StationJobPersistenceJson.CreateOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationMaterialArrivalOutboxStore(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "Station material outbox requires a file-backed SQLite data source.",
                nameof(connectionString));
        }

        var path = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        builder.DataSource = path;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        _connectionString = builder.ToString();
    }

    public async ValueTask<bool> TryEnqueueAsync(
        MaterialArrived message,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        ValidateUtc(receivedAtUtc, nameof(receivedAtUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(message, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO station_material_arrival_outbox (
                message_id, idempotency_key, payload_json, created_at_utc,
                attempt_count, last_error, next_attempt_at_utc,
                published_at_utc, quarantined_at_utc)
            VALUES (
                $message_id, $idempotency_key, $payload_json, $created_at_utc,
                0, NULL, $created_at_utc, NULL, NULL)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$message_id", message.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$idempotency_key", message.IdempotencyKey);
        command.Parameters.AddWithValue("$payload_json", payload);
        command.Parameters.AddWithValue(
            "$created_at_utc",
            receivedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (!added)
        {
            await EnsureSameAsync(connection, message, payload, cancellationToken)
                .ConfigureAwait(false);
        }

        return added;
    }

    public async ValueTask<IReadOnlyCollection<StationMaterialArrivalOutboxItem>> ListPendingAsync(
        int maximumCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        ValidateUtc(nowUtc, nameof(nowUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, message_id, idempotency_key, payload_json, created_at_utc,
                   attempt_count, next_attempt_at_utc
            FROM station_material_arrival_outbox
            WHERE published_at_utc IS NULL AND quarantined_at_utc IS NULL
            ORDER BY sequence
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var result = new List<StationMaterialArrivalOutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var nextAttemptAtUtc = ParseUtc(reader.GetString(6));
            if (nextAttemptAtUtc > nowUtc)
            {
                break;
            }

            result.Add(new StationMaterialArrivalOutboxItem(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                ParseUtc(reader.GetString(4)),
                reader.GetInt32(5),
                nextAttemptAtUtc));
        }

        return result;
    }

    public ValueTask MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (publishedAtUtc == default || publishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Material publication time must be a non-default UTC value.",
                nameof(publishedAtUtc));
        }

        return ExecuteRequiredAsync(
            "UPDATE station_material_arrival_outbox SET published_at_utc = $value WHERE message_id = $message_id;",
            messageId,
            publishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public ValueTask RecordPublishFailureAsync(
        Guid messageId,
        string failure,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        ValidateUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        return RecordFailureAsync(
            messageId,
            failure,
            nextAttemptAtUtc,
            quarantine: false,
            cancellationToken);
    }

    public ValueTask QuarantineAsync(
        Guid messageId,
        string failure,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        ValidateUtc(quarantinedAtUtc, nameof(quarantinedAtUtc));
        return RecordFailureAsync(
            messageId,
            failure,
            quarantinedAtUtc,
            quarantine: true,
            cancellationToken);
    }

    public void Dispose() => _schemaLock.Dispose();

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaCreated == 1)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                CREATE TABLE IF NOT EXISTS station_material_arrival_outbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL UNIQUE,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    last_error TEXT NULL,
                    next_attempt_at_utc TEXT NOT NULL,
                    published_at_utc TEXT NULL,
                    quarantined_at_utc TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async ValueTask ExecuteRequiredAsync(
        string sql,
        Guid messageId,
        object value,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$message_id", messageId.ToString("D"));
        command.Parameters.AddWithValue("$value", value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Material arrival outbox message {messageId:D} does not exist.");
        }
    }

    private async ValueTask RecordFailureAsync(
        Guid messageId,
        string failure,
        DateTimeOffset occurredAtUtc,
        bool quarantine,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = quarantine
            ? """
                UPDATE station_material_arrival_outbox
                SET attempt_count = attempt_count + 1,
                    last_error = $failure,
                    quarantined_at_utc = $occurred_at_utc
                WHERE message_id = $message_id
                  AND published_at_utc IS NULL
                  AND quarantined_at_utc IS NULL;
                """
            : """
                UPDATE station_material_arrival_outbox
                SET attempt_count = attempt_count + 1,
                    last_error = $failure,
                    next_attempt_at_utc = $occurred_at_utc
                WHERE message_id = $message_id
                  AND published_at_utc IS NULL
                  AND quarantined_at_utc IS NULL;
                """;
        command.Parameters.AddWithValue("$message_id", messageId.ToString("D"));
        command.Parameters.AddWithValue(
            "$failure",
            failure.Length <= 4096 ? failure : failure[..4096]);
        command.Parameters.AddWithValue(
            "$occurred_at_utc",
            occurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Material arrival outbox message {messageId:D} is not pending.");
        }
    }

    private static async ValueTask EnsureSameAsync(
        SqliteConnection connection,
        MaterialArrived message,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, idempotency_key, payload_json
            FROM station_material_arrival_outbox
            WHERE message_id = $message_id OR idempotency_key = $idempotency_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", message.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$idempotency_key", message.IdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(reader.GetString(0), message.MessageId.ToString("D"), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), message.IdempotencyKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Material arrival outbox identity was reused with different evidence.");
        }

        using var existing = JsonDocument.Parse(reader.GetString(2));
        using var candidate = JsonDocument.Parse(payload);
        if (!JsonElement.DeepEquals(existing.RootElement, candidate.RootElement))
        {
            throw new InvalidOperationException(
                "Material arrival outbox identity was reused with different evidence.");
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.ParseExact(
        value,
        "O",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None);

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Station material arrival outbox timestamp must be non-default UTC.",
                parameterName);
        }
    }
}
