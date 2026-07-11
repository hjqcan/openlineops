using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteProductionMaterialArrivalInbox :
    IProductionMaterialArrivalInbox,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteProductionMaterialArrivalInbox(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "Material arrival Inbox requires a file-backed SQLite data source.",
                nameof(connectionString));
        }

        var path = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        builder.DataSource = path;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        _connectionString = builder.ToString();
    }

    public async ValueTask<ProductionMaterialArrivalClaim> ClaimAsync(
        MaterialArrived message,
        DateTimeOffset claimedAtUtc,
        TimeSpan claimDuration,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        ValidateTimestamp(claimedAtUtc, nameof(claimedAtUtc));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            claimDuration,
            TimeSpan.Zero);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(message, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO production_material_arrival_inbox (
                    message_id, idempotency_key, payload_json, claim_token, claim_until_utc,
                    result_json, completed_at_utc)
                VALUES ($message_id, $idempotency_key, $payload_json, NULL, NULL, NULL, NULL)
                ON CONFLICT DO NOTHING;
                """;
            AddIdentity(insert, message);
            insert.Parameters.AddWithValue("$payload_json", payload);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT message_id, idempotency_key, payload_json, claim_token, claim_until_utc,
                   result_json
            FROM production_material_arrival_inbox
            WHERE message_id = $message_id OR idempotency_key = $idempotency_key
            LIMIT 1;
            """;
        AddIdentity(select, message);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Material arrival Inbox row was not created.");
        }

        EnsureSame(message, payload, reader);
        var claimTokenText = reader.IsDBNull(3) ? null : reader.GetString(3);
        DateTimeOffset? claimUntil = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4));
        var resultJson = reader.IsDBNull(5) ? null : reader.GetString(5);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (resultJson is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProductionMaterialArrivalClaim(
                ProductionMaterialArrivalClaimStatus.Completed,
                null,
                null,
                DeserializeResult(resultJson));
        }

        if (claimTokenText is not null && claimUntil > claimedAtUtc)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProductionMaterialArrivalClaim(
                ProductionMaterialArrivalClaimStatus.Busy,
                null,
                claimUntil,
                null);
        }

        var claimToken = Guid.NewGuid();
        var claimUntilUtc = claimedAtUtc.Add(claimDuration);
        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE production_material_arrival_inbox
                SET claim_token = $claim_token,
                    claim_until_utc = $claim_until_utc
                WHERE message_id = $message_id AND result_json IS NULL;
                """;
            claim.Parameters.AddWithValue("$claim_token", claimToken.ToString("D"));
            claim.Parameters.AddWithValue("$claim_until_utc", FormatUtc(claimUntilUtc));
            claim.Parameters.AddWithValue("$message_id", message.MessageId.ToString("D"));
            if (await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("Material arrival Inbox claim was not persisted.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionMaterialArrivalClaim(
            ProductionMaterialArrivalClaimStatus.Claimed,
            claimToken,
            null,
            null);
    }

    public async ValueTask CompleteAsync(
        Guid messageId,
        Guid claimToken,
        RuntimeOperationResult result,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty || claimToken == Guid.Empty)
        {
            throw new ArgumentException("Material arrival completion identity is incomplete.");
        }

        ArgumentNullException.ThrowIfNull(result);
        ValidateTimestamp(completedAtUtc, nameof(completedAtUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT claim_token, result_json
            FROM production_material_arrival_inbox
            WHERE message_id = $message_id;
            """;
        select.Parameters.AddWithValue("$message_id", messageId.ToString("D"));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Material arrival Inbox message {messageId:D} does not exist.");
        }

        var persistedToken = reader.IsDBNull(0) ? null : reader.GetString(0);
        var persistedResult = reader.IsDBNull(1) ? null : reader.GetString(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (persistedResult is not null)
        {
            using var existing = JsonDocument.Parse(persistedResult);
            using var candidate = JsonDocument.Parse(resultJson);
            if (!JsonElement.DeepEquals(existing.RootElement, candidate.RootElement))
            {
                throw new InvalidOperationException(
                    $"Material arrival message {messageId:D} was completed with different evidence.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(persistedToken, claimToken.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Material arrival message {messageId:D} is owned by a different claim.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE production_material_arrival_inbox
            SET result_json = $result_json,
                completed_at_utc = $completed_at_utc,
                claim_token = NULL,
                claim_until_utc = NULL
            WHERE message_id = $message_id AND claim_token = $claim_token;
            """;
        update.Parameters.AddWithValue("$result_json", resultJson);
        update.Parameters.AddWithValue("$completed_at_utc", FormatUtc(completedAtUtc));
        update.Parameters.AddWithValue("$message_id", messageId.ToString("D"));
        update.Parameters.AddWithValue("$claim_token", claimToken.ToString("D"));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Material arrival message {messageId:D} claim changed before completion.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
                CREATE TABLE IF NOT EXISTS production_material_arrival_inbox (
                    message_id TEXT PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    payload_json TEXT NOT NULL,
                    claim_token TEXT NULL,
                    claim_until_utc TEXT NULL,
                    result_json TEXT NULL,
                    completed_at_utc TEXT NULL
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

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddIdentity(SqliteCommand command, MaterialArrived message)
    {
        command.Parameters.AddWithValue("$message_id", message.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$idempotency_key", message.IdempotencyKey);
    }

    private static void EnsureSame(
        MaterialArrived message,
        string payload,
        SqliteDataReader reader)
    {
        if (!string.Equals(reader.GetString(0), message.MessageId.ToString("D"), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), message.IdempotencyKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Material arrival Inbox identity was reused with different evidence.");
        }

        using var existing = JsonDocument.Parse(reader.GetString(2));
        using var candidate = JsonDocument.Parse(payload);
        if (!JsonElement.DeepEquals(existing.RootElement, candidate.RootElement))
        {
            throw new InvalidOperationException(
                "Material arrival Inbox identity was reused with different evidence.");
        }
    }

    private static RuntimeOperationResult DeserializeResult(string json) =>
        JsonSerializer.Deserialize<RuntimeOperationResult>(json, JsonOptions)
        ?? throw new InvalidDataException("Material arrival Inbox result is empty.");

    private static void ValidateTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Material arrival Inbox timestamp must be a non-default UTC value.",
                parameterName);
        }
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.ParseExact(
        value,
        "O",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None);
}
