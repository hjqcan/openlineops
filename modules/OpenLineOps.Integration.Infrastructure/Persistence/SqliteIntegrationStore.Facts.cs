using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.WorkOrders;

namespace OpenLineOps.Integration.Infrastructure.Persistence;

public sealed partial class SqliteIntegrationStore
{
    public async ValueTask AppendAsync(
        IEnumerable<WorkOrderFact> facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var snapshot = facts.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(static fact => fact is null))
        {
            throw new ArgumentException(
                "At least one non-null work order fact is required.",
                nameof(facts));
        }

        var workOrderId = snapshot[0].WorkOrderId;
        if (snapshot.Any(fact => fact.WorkOrderId != workOrderId))
        {
            throw new ArgumentException(
                "One append operation can contain facts for only one work order.",
                nameof(facts));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        var persisted = await ReadFactsAsync(
                connection,
                transaction,
                workOrderId,
                cancellationToken)
            .ConfigureAwait(false);
        var accepted = persisted.ToList();
        var byId = accepted.ToDictionary(static fact => fact.Id.Value, StringComparer.Ordinal);
        var bySequence = accepted.ToDictionary(static fact => fact.Sequence);
        var newFacts = new List<WorkOrderFact>();
        foreach (var fact in snapshot.OrderBy(static fact => fact.Sequence))
        {
            byId.TryGetValue(fact.Id.Value, out var idMatch);
            bySequence.TryGetValue(fact.Sequence, out var sequenceMatch);
            if (idMatch is not null || sequenceMatch is not null)
            {
                if (idMatch is null
                    || sequenceMatch is null
                    || !ReferenceEquals(idMatch, sequenceMatch)
                    || idMatch != fact)
                {
                    throw new IntegrationMessageConflictException(
                        $"Work order fact '{fact.Id.Value}' conflicts with appended production facts.");
                }

                continue;
            }

            accepted.Add(fact);
            newFacts.Add(fact);
            byId.Add(fact.Id.Value, fact);
            bySequence.Add(fact.Sequence, fact);
        }

        _ = WorkOrder.Rehydrate(accepted);
        foreach (var fact in newFacts.OrderBy(static fact => fact.Sequence))
        {
            var fingerprint = FactFingerprint(fact);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO integration_work_order_facts (
                    fact_id, work_order_id, fact_sequence, kind, resulting_status,
                    occurred_at_utc, actor_id, product_model_id, target_quantity,
                    reason, content_sha256)
                VALUES (
                    $fact_id, $work_order_id, $fact_sequence, $kind, $resulting_status,
                    $occurred_at_utc, $actor_id, $product_model_id, $target_quantity,
                    $reason, $content_sha256)
                ON CONFLICT DO NOTHING;
                """;
            AddFactParameters(insert, fact, fingerprint);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await EnsureFactMatchesAsync(
                        connection,
                        transaction,
                        fact,
                        fingerprint,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WorkOrderFact>> ListAsync(
        WorkOrderId workOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workOrderId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT fact_id, work_order_id, fact_sequence, kind, resulting_status,
                   occurred_at_utc, actor_id, product_model_id, target_quantity, reason
            FROM integration_work_order_facts
            WHERE work_order_id = $work_order_id
            ORDER BY fact_sequence;
            """;
        command.Parameters.AddWithValue("$work_order_id", workOrderId.Value);
        var result = new List<WorkOrderFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadFact(reader));
        }

        return result;
    }

    private static async ValueTask EnsureFactMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkOrderFact fact,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fact_id, work_order_id, fact_sequence, content_sha256
            FROM integration_work_order_facts
            WHERE fact_id = $fact_id
               OR (work_order_id = $work_order_id AND fact_sequence = $fact_sequence)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$fact_id", fact.Id.Value);
        command.Parameters.AddWithValue("$work_order_id", fact.WorkOrderId.Value);
        command.Parameters.AddWithValue("$fact_sequence", fact.Sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(reader.GetString(0), fact.Id.Value, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), fact.WorkOrderId.Value, StringComparison.Ordinal)
            || reader.GetInt64(2) != fact.Sequence
            || !string.Equals(reader.GetString(3), fingerprint, StringComparison.Ordinal))
        {
            throw new IntegrationMessageConflictException(
                $"Work order fact '{fact.Id.Value}' conflicts with appended production facts.");
        }
    }

    private static async ValueTask<IReadOnlyList<WorkOrderFact>> ReadFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkOrderId workOrderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fact_id, work_order_id, fact_sequence, kind, resulting_status,
                   occurred_at_utc, actor_id, product_model_id, target_quantity, reason
            FROM integration_work_order_facts
            WHERE work_order_id = $work_order_id
            ORDER BY fact_sequence;
            """;
        command.Parameters.AddWithValue("$work_order_id", workOrderId.Value);
        var result = new List<WorkOrderFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadFact(reader));
        }

        return result;
    }

    private static void AddFactParameters(
        SqliteCommand command,
        WorkOrderFact fact,
        string fingerprint)
    {
        command.Parameters.AddWithValue("$fact_id", fact.Id.Value);
        command.Parameters.AddWithValue("$work_order_id", fact.WorkOrderId.Value);
        command.Parameters.AddWithValue("$fact_sequence", fact.Sequence);
        command.Parameters.AddWithValue("$kind", fact.Kind.ToString());
        command.Parameters.AddWithValue("$resulting_status", fact.ResultingStatus.ToString());
        command.Parameters.AddWithValue("$occurred_at_utc", FormatUtc(fact.OccurredAtUtc));
        command.Parameters.AddWithValue("$actor_id", fact.ActorId);
        command.Parameters.AddWithValue(
            "$product_model_id",
            fact.ProductModelId is null ? DBNull.Value : fact.ProductModelId);
        command.Parameters.AddWithValue(
            "$target_quantity",
            fact.TargetQuantity is null ? DBNull.Value : fact.TargetQuantity.Value);
        command.Parameters.AddWithValue("$reason", fact.Reason is null ? DBNull.Value : fact.Reason);
        command.Parameters.AddWithValue("$content_sha256", fingerprint);
    }

    private static WorkOrderFact ReadFact(SqliteDataReader reader)
    {
        return new WorkOrderFact(
            new WorkOrderFactId(reader.GetString(0)),
            new WorkOrderId(reader.GetString(1)),
            reader.GetInt64(2),
            ParseEnum<WorkOrderFactKind>(reader.GetString(3)),
            ParseEnum<WorkOrderStatus>(reader.GetString(4)),
            ParseUtc(reader.GetString(5)),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private static string FactFingerprint(WorkOrderFact fact)
    {
        var content = string.Join(
            "\n",
            fact.Id.Value,
            fact.WorkOrderId.Value,
            fact.Sequence.ToString(CultureInfo.InvariantCulture),
            fact.Kind.ToString(),
            fact.ResultingStatus.ToString(),
            fact.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
            fact.ActorId,
            fact.ProductModelId ?? "<null>",
            fact.TargetQuantity?.ToString(CultureInfo.InvariantCulture) ?? "<null>",
            fact.Reason ?? "<null>");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
               && Enum.IsDefined(parsed)
               && string.Equals(value, parsed.ToString(), StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException(
                $"Persisted enum token '{value}' is invalid.");
    }
}
