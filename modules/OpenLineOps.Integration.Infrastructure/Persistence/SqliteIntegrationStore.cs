using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Application.Queries;
using OpenLineOps.Integration.Application.Serialization;
using OpenLineOps.Integration.Application.WorkOrders;

namespace OpenLineOps.Integration.Infrastructure.Persistence;

public sealed partial class SqliteIntegrationStore :
    IIntegrationInboxStore,
    IIntegrationOutboxStore,
    IIntegrationQueryStore,
    IWorkOrderFactStore,
    IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteIntegrationStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "Integration persistence requires a file-backed SQLite data source.",
                nameof(connectionString));
        }

        var path = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new ArgumentException(
                "Integration SQLite path has no parent directory.",
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
        _schemaLock.Dispose();
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

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS integration_inbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL UNIQUE,
                    content_sha256 TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    status TEXT NOT NULL,
                    processing_token TEXT NOT NULL,
                    processing_lease_expires_at_utc TEXT NOT NULL,
                    response_message_id TEXT NULL,
                    response_json TEXT NULL,
                    response_sha256 TEXT NULL,
                    completed_at_utc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS integration_inbox_claim_audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    processing_token TEXT NOT NULL UNIQUE,
                    claimed_at_utc TEXT NOT NULL,
                    lease_expires_at_utc TEXT NOT NULL,
                    reclaimed INTEGER NOT NULL CHECK(reclaimed IN (0, 1)),
                    FOREIGN KEY(message_id) REFERENCES integration_inbox(message_id)
                );

                CREATE TABLE IF NOT EXISTS integration_outbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL UNIQUE,
                    correlation_id TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    next_attempt_at_utc TEXT NOT NULL,
                    last_error TEXT NULL,
                    delivered_at_utc TEXT NULL,
                    dead_lettered_at_utc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_integration_outbox_delivery
                    ON integration_outbox(delivered_at_utc, dead_lettered_at_utc, sequence);

                CREATE TABLE IF NOT EXISTS integration_outbox_failure_audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    failure TEXT NOT NULL,
                    failed_at_utc TEXT NOT NULL,
                    next_attempt_at_utc TEXT NOT NULL,
                    dead_lettered INTEGER NOT NULL CHECK(dead_lettered IN (0, 1)),
                    FOREIGN KEY(message_id) REFERENCES integration_outbox(message_id)
                );

                CREATE TABLE IF NOT EXISTS integration_outbox_replay_audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    replayed_at_utc TEXT NOT NULL,
                    previous_attempt_count INTEGER NOT NULL,
                    FOREIGN KEY(message_id) REFERENCES integration_outbox(message_id)
                );

                CREATE TABLE IF NOT EXISTS integration_work_order_facts (
                    fact_id TEXT NOT NULL PRIMARY KEY,
                    work_order_id TEXT NOT NULL,
                    fact_sequence INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    resulting_status TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    product_model_id TEXT NULL,
                    target_quantity INTEGER NULL,
                    reason TEXT NULL,
                    content_sha256 TEXT NOT NULL,
                    UNIQUE(work_order_id, fact_sequence)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await UpgradeInboxLeaseSchemaAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await UpgradeInboxIntegritySchemaAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask UpgradeInboxLeaseSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(
                connection,
                "processing_token",
                cancellationToken)
            .ConfigureAwait(false))
        {
            await using var addToken = connection.CreateCommand();
            addToken.CommandText =
                "ALTER TABLE integration_inbox ADD COLUMN processing_token TEXT NULL;";
            await addToken.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(
                connection,
                "processing_lease_expires_at_utc",
                cancellationToken)
            .ConfigureAwait(false))
        {
            await using var addLease = connection.CreateCommand();
            addLease.CommandText =
                "ALTER TABLE integration_inbox ADD COLUMN processing_lease_expires_at_utc TEXT NULL;";
            await addLease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var backfill = connection.CreateCommand();
        backfill.CommandText = """
            UPDATE integration_inbox
            SET processing_token =
                    COALESCE(processing_token, 'legacy-' || message_id),
                processing_lease_expires_at_utc =
                    COALESCE(processing_lease_expires_at_utc, received_at_utc)
            WHERE processing_token IS NULL
               OR processing_lease_expires_at_utc IS NULL;
            """;
        await backfill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpgradeInboxIntegritySchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(
                connection,
                "response_sha256",
                cancellationToken)
            .ConfigureAwait(false))
        {
            await using var addResponseHash = connection.CreateCommand();
            addResponseHash.CommandText =
                "ALTER TABLE integration_inbox ADD COLUMN response_sha256 TEXT NULL;";
            await addResponseHash.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        var candidates = new List<LegacyCompletedInboxEvidence>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT inbox.message_id,
                       inbox.content_sha256,
                       inbox.request_json,
                       inbox.response_message_id,
                       inbox.response_json,
                       outbox.correlation_id,
                       outbox.content_sha256,
                       outbox.payload_json
                FROM integration_inbox AS inbox
                LEFT JOIN integration_outbox AS outbox
                  ON outbox.message_id = inbox.response_message_id
                WHERE inbox.status = 'Completed'
                  AND inbox.response_sha256 IS NULL;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new LegacyCompletedInboxEvidence(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        foreach (var candidate in candidates)
        {
            var responseSha256 = TryComputeTrustedLegacyResponseSha256(candidate);
            if (responseSha256 is null)
            {
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE integration_inbox
                SET response_sha256 = $response_sha256
                WHERE message_id = $message_id
                  AND status = 'Completed'
                  AND response_sha256 IS NULL
                  AND content_sha256 = $request_sha256
                  AND request_json = $request_json
                  AND response_message_id = $response_message_id
                  AND response_json = $response_json
                  AND EXISTS (
                      SELECT 1
                      FROM integration_outbox
                      WHERE message_id = $response_message_id
                        AND correlation_id = $message_id
                        AND content_sha256 = $response_sha256
                        AND payload_json = $response_json);
                """;
            update.Parameters.AddWithValue("$response_sha256", responseSha256);
            update.Parameters.AddWithValue("$message_id", candidate.MessageId);
            update.Parameters.AddWithValue("$request_sha256", candidate.RequestSha256);
            update.Parameters.AddWithValue("$request_json", candidate.RequestJson);
            update.Parameters.AddWithValue(
                "$response_message_id",
                candidate.ResponseMessageId!);
            update.Parameters.AddWithValue("$response_json", candidate.ResponseJson!);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? TryComputeTrustedLegacyResponseSha256(
        LegacyCompletedInboxEvidence evidence)
    {
        if (evidence.ResponseMessageId is null
            || evidence.ResponseJson is null
            || evidence.OutboxCorrelationId is null
            || evidence.OutboxContentSha256 is null
            || evidence.OutboxPayloadJson is null)
        {
            return null;
        }

        try
        {
            if (!string.Equals(
                    IntegrationMessageCodec.ComputeSha256(evidence.RequestJson),
                    evidence.RequestSha256,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var request = IntegrationMessageCodec.DecodeRequest(evidence.RequestJson);
            var response = IntegrationMessageCodec.DecodeResponse(evidence.ResponseJson);
            var responseSha256 = IntegrationMessageCodec.ComputeSha256(evidence.ResponseJson);
            return string.Equals(
                       IntegrationMessageCodec.Encode(request),
                       evidence.RequestJson,
                       StringComparison.Ordinal)
                   && string.Equals(request.Id.Value, evidence.MessageId, StringComparison.Ordinal)
                   && string.Equals(
                       IntegrationMessageCodec.Encode(response),
                       evidence.ResponseJson,
                       StringComparison.Ordinal)
                   && string.Equals(
                       response.Id.Value,
                       evidence.ResponseMessageId,
                       StringComparison.Ordinal)
                   && response.RequestId == request.Id
                   && response.WorkOrderId == request.WorkOrderId
                   && string.Equals(
                       evidence.OutboxCorrelationId,
                       evidence.MessageId,
                       StringComparison.Ordinal)
                   && string.Equals(
                       evidence.OutboxContentSha256,
                       responseSha256,
                       StringComparison.Ordinal)
                   && string.Equals(
                       evidence.OutboxPayloadJson,
                       evidence.ResponseJson,
                       StringComparison.Ordinal)
                ? responseSha256
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async ValueTask<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('integration_inbox')
            WHERE name = $column_name;
            """;
        command.Parameters.AddWithValue("$column_name", columnName);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L) == 1;
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        ValidateUtc(value, nameof(value));
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.TryParseExact(
                   value,
                   "O",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed)
               && parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw new InvalidDataException(
                $"Persisted Integration timestamp '{value}' is not canonical UTC.");
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Integration timestamp must be non-default UTC.",
                parameterName);
        }
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
               || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }

    private sealed record LegacyCompletedInboxEvidence(
        string MessageId,
        string RequestSha256,
        string RequestJson,
        string? ResponseMessageId,
        string? ResponseJson,
        string? OutboxCorrelationId,
        string? OutboxContentSha256,
        string? OutboxPayloadJson);
}
