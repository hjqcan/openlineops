using Microsoft.Data.Sqlite;
using OpenLineOps.Integration.Application.Queries;
using OpenLineOps.Integration.Application.Serialization;
using OpenLineOps.Integration.Domain.Identifiers;

namespace OpenLineOps.Integration.Infrastructure.Persistence;

public sealed partial class SqliteIntegrationStore
{
    public async ValueTask<IntegrationWorkRequestSnapshot?> GetWorkRequestAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_json, content_sha256, received_at_utc, status, response_message_id,
                   completed_at_utc
            FROM integration_inbox
            WHERE message_id = $message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", requestId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var requestJson = reader.GetString(0);
        if (!string.Equals(
                IntegrationMessageCodec.ComputeSha256(requestJson),
                reader.GetString(1),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Inbox record '{requestId.Value}' failed its content hash check.");
        }

        var request = IntegrationMessageCodec.DecodeRequest(requestJson);
        if (request.Id != requestId)
        {
            throw new InvalidDataException(
                $"Inbox record '{requestId.Value}' contains a different request identity.");
        }

        var state = reader.GetString(3) switch
        {
            "Pending" => IntegrationInboxState.Pending,
            "Completed" => IntegrationInboxState.Completed,
            var value => throw new InvalidDataException(
                $"Inbox record '{requestId.Value}' has invalid state '{value}'.")
        };
        var responseMessageId = reader.IsDBNull(4) ? null : reader.GetString(4);
        DateTimeOffset? completedAtUtc = reader.IsDBNull(5)
            ? null
            : ParseUtc(reader.GetString(5));
        if ((state == IntegrationInboxState.Pending
                && (responseMessageId is not null || completedAtUtc is not null))
            || (state == IntegrationInboxState.Completed
                && (responseMessageId is null || completedAtUtc is null)))
        {
            throw new InvalidDataException(
                $"Inbox record '{requestId.Value}' has an inconsistent completion shape.");
        }

        return new IntegrationWorkRequestSnapshot(
            request,
            ParseUtc(reader.GetString(2)),
            state,
            responseMessageId,
            completedAtUtc);
    }

    public async ValueTask<IntegrationWorkResponseSnapshot?> GetWorkResponseAsync(
        WorkResponseId responseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responseId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, payload_json, content_sha256, attempt_count, next_attempt_at_utc,
                   last_error, delivered_at_utc, dead_lettered_at_utc
            FROM integration_outbox
            WHERE message_id = $message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message_id", responseId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var responseJson = reader.GetString(1);
        if (!string.Equals(
                IntegrationMessageCodec.ComputeSha256(responseJson),
                reader.GetString(2),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Outbox record '{responseId.Value}' failed its content hash check.");
        }

        var response = IntegrationMessageCodec.DecodeResponse(responseJson);
        if (response.Id != responseId)
        {
            throw new InvalidDataException(
                $"Outbox record '{responseId.Value}' contains a different response identity.");
        }

        return new IntegrationWorkResponseSnapshot(
            response,
            reader.GetInt64(0),
            reader.GetInt32(3),
            ParseUtc(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)));
    }

    public async ValueTask<IReadOnlyList<IntegrationDeadLetterSnapshot>>
        ListDeadLettersAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        if (maximumCount > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                "At most 500 dead letters can be read at once.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, message_id, correlation_id, attempt_count, last_error,
                   created_at_utc, dead_lettered_at_utc
            FROM integration_outbox
            WHERE delivered_at_utc IS NULL
              AND dead_lettered_at_utc IS NOT NULL
            ORDER BY sequence
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var result = new List<IntegrationDeadLetterSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(4))
            {
                throw new InvalidDataException(
                    $"Dead letter '{reader.GetString(1)}' does not contain a failure.");
            }

            result.Add(new IntegrationDeadLetterSnapshot(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                ParseUtc(reader.GetString(5)),
                ParseUtc(reader.GetString(6))));
        }

        return result;
    }
}
