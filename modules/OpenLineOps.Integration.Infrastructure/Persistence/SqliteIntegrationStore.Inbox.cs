using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Serialization;
using OpenLineOps.Integration.Domain.Messages;

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
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO integration_inbox (
                    message_id, content_sha256, request_json, received_at_utc, status,
                    processing_token, processing_lease_expires_at_utc,
                    response_message_id, response_json, response_sha256, completed_at_utc)
                VALUES (
                    $message_id, $content_sha256, $request_json, $received_at_utc, 'Pending',
                    $processing_token, $processing_lease_expires_at_utc,
                    NULL, NULL, NULL, NULL)
                ON CONFLICT(message_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$message_id", message.MessageId);
            insert.Parameters.AddWithValue("$content_sha256", message.ContentSha256);
            insert.Parameters.AddWithValue("$request_json", message.CanonicalContent);
            insert.Parameters.AddWithValue("$received_at_utc", FormatUtc(message.ReceivedAtUtc));
            insert.Parameters.AddWithValue("$processing_token", message.ProcessingToken);
            insert.Parameters.AddWithValue(
                "$processing_lease_expires_at_utc",
                FormatUtc(message.ProcessingLeaseExpiresAtUtc));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                await InsertClaimAuditAsync(
                        connection,
                        transaction,
                        message,
                        reclaimed: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new IntegrationInboxBeginResult(
                    IntegrationInboxDisposition.Started,
                    null,
                    message.ProcessingToken);
            }
        }

        var result = await ReadOrReclaimExistingInboxAsync(
                connection,
                transaction,
                message,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
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
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.ResponseSha256,
                    completion.ResponseSha256,
                    StringComparison.Ordinal))
            {
                throw new IntegrationMessageConflictException(
                    $"Inbox message '{completion.MessageId}' was replayed with a different response.");
            }

            await ValidateCompletedInboxEvidenceAsync(
                    connection,
                    transaction,
                    completion.MessageId,
                    existing.ContentSha256,
                    existing.RequestJson,
                    existing.ResponseMessageId,
                    existing.ResponseJson,
                    existing.ResponseSha256,
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

        ValidateResponseAgainstRequest(
            completion.MessageId,
            existing.ContentSha256,
            existing.RequestJson,
            completion.ResponseMessageId,
            completion.ResponseJson,
            completion.ResponseSha256);

        if (!string.Equals(
                existing.ProcessingToken,
                completion.ProcessingToken,
                StringComparison.Ordinal))
        {
            throw new IntegrationInboxLeaseLostException(
                $"Inbox message '{completion.MessageId}' is owned by a newer processing claim.");
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE integration_inbox
                SET status = 'Completed',
                    response_message_id = $response_message_id,
                    response_json = $response_json,
                    response_sha256 = $response_sha256,
                    completed_at_utc = $completed_at_utc
                WHERE message_id = $message_id
                  AND status = 'Pending'
                  AND content_sha256 = $content_sha256
                  AND processing_token = $processing_token;
                """;
            update.Parameters.AddWithValue("$response_message_id", completion.ResponseMessageId);
            update.Parameters.AddWithValue("$response_json", completion.ResponseJson);
            update.Parameters.AddWithValue("$response_sha256", completion.ResponseSha256);
            update.Parameters.AddWithValue("$completed_at_utc", FormatUtc(completion.CompletedAtUtc));
            update.Parameters.AddWithValue("$message_id", completion.MessageId);
            update.Parameters.AddWithValue("$content_sha256", completion.ContentSha256);
            update.Parameters.AddWithValue("$processing_token", completion.ProcessingToken);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new IntegrationInboxLeaseLostException(
                    $"Inbox message '{completion.MessageId}' lost its processing claim before completion.");
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
                completion.ResponseSha256);
            outbox.Parameters.AddWithValue("$payload_json", completion.ResponseJson);
            outbox.Parameters.AddWithValue("$created_at_utc", FormatUtc(completion.CompletedAtUtc));
            await outbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ValidateCompletedInboxEvidenceAsync(
                connection,
                transaction,
                completion.MessageId,
                existing.ContentSha256,
                existing.RequestJson,
                completion.ResponseMessageId,
                completion.ResponseJson,
                completion.ResponseSha256,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<IntegrationInboxClaimAudit>> ListClaimAuditAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        messageId = Required(messageId, nameof(messageId));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, message_id, actor_id, processing_token, claimed_at_utc,
                   lease_expires_at_utc, reclaimed
            FROM integration_inbox_claim_audit
            WHERE message_id = $message_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);
        var result = new List<IntegrationInboxClaimAudit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new IntegrationInboxClaimAudit(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseUtc(reader.GetString(4)),
                ParseUtc(reader.GetString(5)),
                reader.GetInt64(6) == 1));
        }

        return result;
    }

    private static async ValueTask<IntegrationInboxBeginResult>
        ReadOrReclaimExistingInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntegrationInboundMessage message,
        CancellationToken cancellationToken)
    {
        string contentSha256;
        string requestJson;
        string status;
        string? responseMessageId;
        string? responseJson;
        string? responseSha256;
        string processingToken;
        DateTimeOffset processingLeaseExpiresAtUtc;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT content_sha256, request_json, status, response_message_id,
                   response_json, response_sha256, processing_token,
                   processing_lease_expires_at_utc
            FROM integration_inbox
            WHERE message_id = $message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", message.MessageId);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Inbox message '{message.MessageId}' disappeared after insert conflict.");
            }

            contentSha256 = reader.GetString(0);
            requestJson = reader.GetString(1);
            status = reader.GetString(2);
            responseMessageId = reader.IsDBNull(3) ? null : reader.GetString(3);
            responseJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            responseSha256 = reader.IsDBNull(5) ? null : reader.GetString(5);
            processingToken = reader.GetString(6);
            processingLeaseExpiresAtUtc = ParseUtc(reader.GetString(7));
        }

        if (!string.Equals(contentSha256, message.ContentSha256, StringComparison.Ordinal)
            || !string.Equals(requestJson, message.CanonicalContent, StringComparison.Ordinal))
        {
            throw new IntegrationMessageConflictException(
                $"Inbox message id '{message.MessageId}' was reused with different content.");
        }

        if (status == "Completed")
        {
            await ValidateCompletedInboxEvidenceAsync(
                    connection,
                    transaction,
                    message.MessageId,
                    contentSha256,
                    requestJson,
                    responseMessageId,
                    responseJson,
                    responseSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            return new IntegrationInboxBeginResult(
                IntegrationInboxDisposition.Replayed,
                responseJson,
                null)
            {
                ResponseMessageId = responseMessageId,
                ResponseSha256 = responseSha256
            };
        }

        if (status != "Pending")
        {
            throw new InvalidDataException(
                $"Inbox message '{message.MessageId}' has invalid persisted state '{status}'.");
        }

        if (processingLeaseExpiresAtUtc > message.ReceivedAtUtc)
        {
            return new IntegrationInboxBeginResult(
                IntegrationInboxDisposition.InProgress,
                null,
                null);
        }

        await using (var reclaim = connection.CreateCommand())
        {
            reclaim.Transaction = transaction;
            reclaim.CommandText = """
                UPDATE integration_inbox
                SET processing_token = $new_processing_token,
                    processing_lease_expires_at_utc = $new_lease_expires_at_utc
                WHERE message_id = $message_id
                  AND status = 'Pending'
                  AND content_sha256 = $content_sha256
                  AND processing_token = $old_processing_token
                  AND processing_lease_expires_at_utc = $old_lease_expires_at_utc;
                """;
            reclaim.Parameters.AddWithValue(
                "$new_processing_token",
                message.ProcessingToken);
            reclaim.Parameters.AddWithValue(
                "$new_lease_expires_at_utc",
                FormatUtc(message.ProcessingLeaseExpiresAtUtc));
            reclaim.Parameters.AddWithValue("$message_id", message.MessageId);
            reclaim.Parameters.AddWithValue("$content_sha256", message.ContentSha256);
            reclaim.Parameters.AddWithValue("$old_processing_token", processingToken);
            reclaim.Parameters.AddWithValue(
                "$old_lease_expires_at_utc",
                FormatUtc(processingLeaseExpiresAtUtc));
            if (await reclaim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Inbox message '{message.MessageId}' could not reclaim its expired processing lease.");
            }
        }

        await InsertClaimAuditAsync(
                connection,
                transaction,
                message,
                reclaimed: true,
                cancellationToken)
            .ConfigureAwait(false);
        return new IntegrationInboxBeginResult(
            IntegrationInboxDisposition.Started,
            null,
            message.ProcessingToken);
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
            SELECT content_sha256, request_json, status, response_message_id,
                   response_json, response_sha256, processing_token
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
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6))
            : throw new InvalidOperationException(
                $"Inbox message '{messageId}' does not exist.");
    }

    private static async ValueTask InsertClaimAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntegrationInboundMessage message,
        bool reclaimed,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO integration_inbox_claim_audit (
                message_id, actor_id, processing_token, claimed_at_utc,
                lease_expires_at_utc, reclaimed)
            VALUES (
                $message_id, $actor_id, $processing_token, $claimed_at_utc,
                $lease_expires_at_utc, $reclaimed);
            """;
        command.Parameters.AddWithValue("$message_id", message.MessageId);
        command.Parameters.AddWithValue("$actor_id", message.ActorId);
        command.Parameters.AddWithValue("$processing_token", message.ProcessingToken);
        command.Parameters.AddWithValue("$claimed_at_utc", FormatUtc(message.ReceivedAtUtc));
        command.Parameters.AddWithValue(
            "$lease_expires_at_utc",
            FormatUtc(message.ProcessingLeaseExpiresAtUtc));
        command.Parameters.AddWithValue("$reclaimed", reclaimed ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ValidateCompletedInboxEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        string requestSha256,
        string requestJson,
        string? responseMessageId,
        string? responseJson,
        string? responseSha256,
        CancellationToken cancellationToken)
    {
        if (responseMessageId is null
            || responseJson is null
            || responseSha256 is null)
        {
            throw new InvalidDataException(
                $"Completed Inbox message '{messageId}' has incomplete response evidence.");
        }

        ValidateResponseAgainstRequest(
            messageId,
            requestSha256,
            requestJson,
            responseMessageId,
            responseJson,
            responseSha256);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT correlation_id, content_sha256, payload_json
            FROM integration_outbox
            WHERE message_id = $message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", responseMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(reader.GetString(0), messageId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), responseSha256, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), responseJson, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Outbox response '{responseMessageId}' does not match completed Inbox evidence.");
        }
    }

    private static void ValidateResponseAgainstRequest(
        string messageId,
        string requestSha256,
        string requestJson,
        string responseMessageId,
        string responseJson,
        string responseSha256)
    {
        try
        {
            if (!string.Equals(
                    IntegrationMessageCodec.ComputeSha256(requestJson),
                    requestSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Inbox message '{messageId}' failed its request content hash check.");
            }

            var request = IntegrationMessageCodec.DecodeRequest(requestJson);
            if (!string.Equals(
                    IntegrationMessageCodec.Encode(request),
                    requestJson,
                    StringComparison.Ordinal)
                || !string.Equals(request.Id.Value, messageId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Inbox message '{messageId}' has invalid canonical request evidence.");
            }

            if (!string.Equals(
                    IntegrationMessageCodec.ComputeSha256(responseJson),
                    responseSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Completed Inbox message '{messageId}' failed its response content hash check.");
            }

            WorkResponse response = IntegrationMessageCodec.DecodeResponse(responseJson);
            if (!string.Equals(
                    IntegrationMessageCodec.Encode(response),
                    responseJson,
                    StringComparison.Ordinal)
                || !string.Equals(
                    response.Id.Value,
                    responseMessageId,
                    StringComparison.Ordinal)
                || response.RequestId != request.Id
                || response.WorkOrderId != request.WorkOrderId)
            {
                throw new InvalidDataException(
                    $"Completed Inbox message '{messageId}' has invalid response identity evidence.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Inbox message '{messageId}' contains invalid persisted evidence.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Inbox message '{messageId}' contains invalid persisted JSON evidence.",
                exception);
        }
    }

    private sealed record PersistedInboxCompletion(
        string ContentSha256,
        string RequestJson,
        string Status,
        string? ResponseMessageId,
        string? ResponseJson,
        string? ResponseSha256,
        string ProcessingToken);
}
