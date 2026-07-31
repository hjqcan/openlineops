using Microsoft.Data.Sqlite;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Serialization;

namespace OpenLineOps.Integration.Infrastructure.Persistence;

public sealed partial class SqliteIntegrationStore
{
    public async ValueTask<IntegrationInboxBeginResult> TryBeginAsync(
        IntegrationInboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO integration_inbox (
                    message_id, content_sha256, request_json, received_at_utc, status,
                    response_message_id, response_json, completed_at_utc)
                VALUES (
                    $message_id, $content_sha256, $request_json, $received_at_utc, 'Pending',
                    NULL, NULL, NULL)
                ON CONFLICT(message_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$message_id", message.MessageId);
            insert.Parameters.AddWithValue("$content_sha256", message.ContentSha256);
            insert.Parameters.AddWithValue("$request_json", message.CanonicalContent);
            insert.Parameters.AddWithValue("$received_at_utc", FormatUtc(message.ReceivedAtUtc));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                return new IntegrationInboxBeginResult(
                    IntegrationInboxDisposition.Started,
                    null);
            }
        }

        return await ReadExistingInboxAsync(connection, message, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask CompleteAndEnqueueResponseAsync(
        IntegrationInboxCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        var existing = await ReadInboxCompletionStateAsync(
                connection,
                transaction,
                completion.MessageId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                existing.ContentSha256,
                completion.ContentSha256,
                StringComparison.Ordinal))
        {
            throw new IntegrationMessageConflictException(
                $"Inbox message '{completion.MessageId}' was completed with different content.");
        }

        if (existing.Status == "Completed")
        {
            if (!string.Equals(
                    existing.ResponseMessageId,
                    completion.ResponseMessageId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.ResponseJson,
                    completion.ResponseJson,
                    StringComparison.Ordinal))
            {
                throw new IntegrationMessageConflictException(
                    $"Inbox message '{completion.MessageId}' was replayed with a different response.");
            }

            await EnsureOutboxMatchesAsync(
                    connection,
                    transaction,
                    completion,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (existing.Status != "Pending")
        {
            throw new InvalidDataException(
                $"Inbox message '{completion.MessageId}' has invalid state '{existing.Status}'.");
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE integration_inbox
                SET status = 'Completed',
                    response_message_id = $response_message_id,
                    response_json = $response_json,
                    completed_at_utc = $completed_at_utc
                WHERE message_id = $message_id
                  AND status = 'Pending'
                  AND content_sha256 = $content_sha256;
                """;
            update.Parameters.AddWithValue("$response_message_id", completion.ResponseMessageId);
            update.Parameters.AddWithValue("$response_json", completion.ResponseJson);
            update.Parameters.AddWithValue("$completed_at_utc", FormatUtc(completion.CompletedAtUtc));
            update.Parameters.AddWithValue("$message_id", completion.MessageId);
            update.Parameters.AddWithValue("$content_sha256", completion.ContentSha256);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Inbox message '{completion.MessageId}' could not be completed.");
            }
        }

        await using (var outbox = connection.CreateCommand())
        {
            outbox.Transaction = transaction;
            outbox.CommandText = """
                INSERT INTO integration_outbox (
                    message_id, correlation_id, content_sha256, payload_json,
                    created_at_utc, attempt_count, next_attempt_at_utc,
                    last_error, delivered_at_utc, dead_lettered_at_utc)
                VALUES (
                    $message_id, $correlation_id, $content_sha256, $payload_json,
                    $created_at_utc, 0, $created_at_utc,
                    NULL, NULL, NULL);
                """;
            outbox.Parameters.AddWithValue("$message_id", completion.ResponseMessageId);
            outbox.Parameters.AddWithValue("$correlation_id", completion.MessageId);
            outbox.Parameters.AddWithValue(
                "$content_sha256",
                IntegrationMessageCodec.ComputeSha256(completion.ResponseJson));
            outbox.Parameters.AddWithValue("$payload_json", completion.ResponseJson);
            outbox.Parameters.AddWithValue("$created_at_utc", FormatUtc(completion.CompletedAtUtc));
            await outbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IntegrationInboxBeginResult> ReadExistingInboxAsync(
        SqliteConnection connection,
        IntegrationInboundMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT content_sha256, request_json, status, response_json
            FROM integration_inbox
            WHERE message_id = $message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", message.MessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Inbox message '{message.MessageId}' disappeared after insert conflict.");
        }

        if (!string.Equals(reader.GetString(0), message.ContentSha256, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), message.CanonicalContent, StringComparison.Ordinal))
        {
            throw new IntegrationMessageConflictException(
                $"Inbox message id '{message.MessageId}' was reused with different content.");
        }

        return reader.GetString(2) switch
        {
            "Pending" => new IntegrationInboxBeginResult(
                IntegrationInboxDisposition.InProgress,
                null),
            "Completed" when !reader.IsDBNull(3) => new IntegrationInboxBeginResult(
                IntegrationInboxDisposition.Replayed,
                reader.GetString(3)),
            var status => throw new InvalidDataException(
                $"Inbox message '{message.MessageId}' has invalid persisted state '{status}'.")
        };
    }

    private static async ValueTask<PersistedInboxCompletion> ReadInboxCompletionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT content_sha256, status, response_message_id, response_json
            FROM integration_inbox
            WHERE message_id = $message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PersistedInboxCompletion(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : throw new InvalidOperationException(
                $"Inbox message '{messageId}' does not exist.");
    }

    private static async ValueTask EnsureOutboxMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntegrationInboxCompletion completion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT correlation_id, payload_json
            FROM integration_outbox
            WHERE message_id = $message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", completion.ResponseMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(reader.GetString(0), completion.MessageId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), completion.ResponseJson, StringComparison.Ordinal))
        {
            throw new IntegrationMessageConflictException(
                $"Outbox response '{completion.ResponseMessageId}' does not match completed Inbox evidence.");
        }
    }

    private sealed record PersistedInboxCompletion(
        string ContentSha256,
        string Status,
        string? ResponseMessageId,
        string? ResponseJson);
}
