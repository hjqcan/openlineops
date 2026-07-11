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
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(message, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO station_material_arrival_outbox (
                message_id, idempotency_key, payload_json, created_at_utc,
                attempt_count, last_error, published_at_utc)
            VALUES (
                $message_id, $idempotency_key, $payload_json, $created_at_utc,
                0, NULL, NULL)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$message_id", message.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$idempotency_key", message.IdempotencyKey);
        command.Parameters.AddWithValue("$payload_json", payload);
        command.Parameters.AddWithValue(
            "$created_at_utc",
            message.ArrivedAtUtc.ToString("O", CultureInfo.InvariantCulture));
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
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, idempotency_key, payload_json, created_at_utc, attempt_count
            FROM station_material_arrival_outbox
            WHERE published_at_utc IS NULL
            ORDER BY created_at_utc, message_id
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var result = new List<StationMaterialArrivalOutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StationMaterialArrivalOutboxItem(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                ParseUtc(reader.GetString(3)),
                reader.GetInt32(4)));
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        return ExecuteRequiredAsync(
            "UPDATE station_material_arrival_outbox SET attempt_count = attempt_count + 1, last_error = $value WHERE message_id = $message_id;",
            messageId,
            failure.Length <= 4096 ? failure : failure[..4096],
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
                    message_id TEXT PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    last_error TEXT NULL,
                    published_at_utc TEXT NULL
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
}
