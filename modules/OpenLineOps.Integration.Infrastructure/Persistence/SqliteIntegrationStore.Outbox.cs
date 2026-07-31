using Microsoft.Data.Sqlite;
using OpenLineOps.Integration.Application.Outbox;

namespace OpenLineOps.Integration.Infrastructure.Persistence;

public sealed record IntegrationOutboxSnapshot(
    long Sequence,
    string MessageId,
    string CorrelationId,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    string? LastError,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? DeadLetteredAtUtc);

public sealed partial class SqliteIntegrationStore
{
    public async ValueTask<IReadOnlyList<IntegrationOutboundMessage>> ListReadyAsync(
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
            SELECT sequence, message_id, correlation_id, content_sha256, payload_json,
                   created_at_utc, attempt_count, next_attempt_at_utc
            FROM integration_outbox
            WHERE delivered_at_utc IS NULL
              AND dead_lettered_at_utc IS NULL
            ORDER BY sequence
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var result = new List<IntegrationOutboundMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var nextAttemptAtUtc = ParseUtc(reader.GetString(7));
            if (nextAttemptAtUtc > nowUtc)
            {
                break;
            }

            result.Add(new IntegrationOutboundMessage(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseUtc(reader.GetString(5)),
                reader.GetInt32(6),
                nextAttemptAtUtc));
        }

        return result;
    }

    public async ValueTask MarkDeliveredAsync(
        string messageId,
        DateTimeOffset deliveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        Required(messageId, nameof(messageId));
        ValidateUtc(deliveredAtUtc, nameof(deliveredAtUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE integration_outbox
            SET delivered_at_utc = $delivered_at_utc
            WHERE message_id = $message_id
              AND delivered_at_utc IS NULL
              AND dead_lettered_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);
        command.Parameters.AddWithValue("$delivered_at_utc", FormatUtc(deliveredAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            return;
        }

        var snapshot = await GetOutboxAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (snapshot?.DeliveredAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Outbox message '{messageId}' is not pending delivery.");
        }
    }

    public async ValueTask RecordFailureAsync(
        string messageId,
        int expectedAttemptCount,
        string failure,
        DateTimeOffset failedAtUtc,
        DateTimeOffset nextAttemptAtUtc,
        bool deadLetter,
        CancellationToken cancellationToken = default)
    {
        Required(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedAttemptCount);
        failure = Required(failure, nameof(failure));
        ValidateUtc(failedAtUtc, nameof(failedAtUtc));
        ValidateUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        if (nextAttemptAtUtc < failedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAttemptAtUtc),
                "The next delivery attempt cannot precede the failed attempt.");
        }

        var canonicalFailure = failure.Length <= 4096 ? failure : failure[..4096];
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = deadLetter
                ? """
                    UPDATE integration_outbox
                    SET attempt_count = attempt_count + 1,
                        next_attempt_at_utc = $next_attempt_at_utc,
                        last_error = $last_error,
                        dead_lettered_at_utc = $failed_at_utc
                    WHERE message_id = $message_id
                      AND attempt_count = $expected_attempt_count
                      AND delivered_at_utc IS NULL
                      AND dead_lettered_at_utc IS NULL;
                    """
                : """
                    UPDATE integration_outbox
                    SET attempt_count = attempt_count + 1,
                        next_attempt_at_utc = $next_attempt_at_utc,
                        last_error = $last_error
                    WHERE message_id = $message_id
                      AND attempt_count = $expected_attempt_count
                      AND delivered_at_utc IS NULL
                      AND dead_lettered_at_utc IS NULL;
                    """;
            update.Parameters.AddWithValue("$message_id", messageId);
            update.Parameters.AddWithValue("$expected_attempt_count", expectedAttemptCount);
            update.Parameters.AddWithValue(
                "$failed_at_utc",
                FormatUtc(failedAtUtc));
            update.Parameters.AddWithValue(
                "$next_attempt_at_utc",
                FormatUtc(nextAttemptAtUtc));
            update.Parameters.AddWithValue("$last_error", canonicalFailure);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Outbox message '{messageId}' is not pending at attempt {expectedAttemptCount}.");
            }
        }

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO integration_outbox_failure_audit (
                    message_id, attempt_count, failure, failed_at_utc,
                    next_attempt_at_utc, dead_lettered)
                VALUES (
                    $message_id, $attempt_count, $failure, $failed_at_utc,
                    $next_attempt_at_utc, $dead_lettered);
                """;
            audit.Parameters.AddWithValue("$message_id", messageId);
            audit.Parameters.AddWithValue(
                "$attempt_count",
                checked(expectedAttemptCount + 1));
            audit.Parameters.AddWithValue("$failure", canonicalFailure);
            audit.Parameters.AddWithValue("$failed_at_utc", FormatUtc(failedAtUtc));
            audit.Parameters.AddWithValue(
                "$next_attempt_at_utc",
                FormatUtc(nextAttemptAtUtc));
            audit.Parameters.AddWithValue("$dead_lettered", deadLetter ? 1 : 0);
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RequeueDeadLetterAsync(
        string messageId,
        string actorId,
        string reason,
        DateTimeOffset replayedAtUtc,
        CancellationToken cancellationToken = default)
    {
        messageId = Required(messageId, nameof(messageId));
        actorId = Required(actorId, nameof(actorId));
        reason = Required(reason, nameof(reason));
        ValidateUtc(replayedAtUtc, nameof(replayedAtUtc));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        int previousAttemptCount;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT attempt_count
                FROM integration_outbox
                WHERE message_id = $message_id
                  AND delivered_at_utc IS NULL
                  AND dead_lettered_at_utc IS NOT NULL
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$message_id", messageId);
            var scalar = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            previousAttemptCount = scalar is long value
                ? checked((int)value)
                : throw new InvalidOperationException(
                    $"Outbox message '{messageId}' is not a dead letter.");
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE integration_outbox
                SET attempt_count = 0,
                    next_attempt_at_utc = $replayed_at_utc,
                    last_error = NULL,
                    dead_lettered_at_utc = NULL
                WHERE message_id = $message_id
                  AND delivered_at_utc IS NULL
                  AND dead_lettered_at_utc IS NOT NULL;
                """;
            update.Parameters.AddWithValue("$message_id", messageId);
            update.Parameters.AddWithValue("$replayed_at_utc", FormatUtc(replayedAtUtc));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Outbox message '{messageId}' could not be replayed.");
            }
        }

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO integration_outbox_replay_audit (
                    message_id, actor_id, reason, replayed_at_utc, previous_attempt_count)
                VALUES (
                    $message_id, $actor_id, $reason, $replayed_at_utc, $previous_attempt_count);
                """;
            audit.Parameters.AddWithValue("$message_id", messageId);
            audit.Parameters.AddWithValue("$actor_id", actorId);
            audit.Parameters.AddWithValue("$reason", reason);
            audit.Parameters.AddWithValue("$replayed_at_utc", FormatUtc(replayedAtUtc));
            audit.Parameters.AddWithValue("$previous_attempt_count", previousAttemptCount);
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<IntegrationReplayAudit>> ListReplayAuditAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        messageId = Required(messageId, nameof(messageId));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, message_id, actor_id, reason, replayed_at_utc, previous_attempt_count
            FROM integration_outbox_replay_audit
            WHERE message_id = $message_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);
        var result = new List<IntegrationReplayAudit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new IntegrationReplayAudit(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseUtc(reader.GetString(4)),
                reader.GetInt32(5)));
        }

        return result;
    }

    public async ValueTask<IReadOnlyList<IntegrationOutboxFailureAudit>>
        ListFailureAuditAsync(
            string messageId,
            CancellationToken cancellationToken = default)
    {
        messageId = Required(messageId, nameof(messageId));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, message_id, attempt_count, failure, failed_at_utc,
                   next_attempt_at_utc, dead_lettered
            FROM integration_outbox_failure_audit
            WHERE message_id = $message_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);
        var result = new List<IntegrationOutboxFailureAudit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new IntegrationOutboxFailureAudit(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                ParseUtc(reader.GetString(4)),
                ParseUtc(reader.GetString(5)),
                reader.GetInt64(6) == 1));
        }

        return result;
    }

    public async ValueTask<IntegrationOutboxSnapshot?> GetOutboxAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        messageId = Required(messageId, nameof(messageId));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, message_id, correlation_id, attempt_count, next_attempt_at_utc,
                   last_error, delivered_at_utc, dead_lettered_at_utc
            FROM integration_outbox
            WHERE message_id = $message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new IntegrationOutboxSnapshot(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                ParseUtc(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)))
            : null;
    }
}
