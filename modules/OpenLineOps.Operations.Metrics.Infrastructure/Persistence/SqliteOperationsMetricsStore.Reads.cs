using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Operations.Metrics.Domain.Downtime;
using OpenLineOps.Operations.Metrics.Domain.Production;
using OpenLineOps.Operations.Metrics.Domain.Shifts;

namespace OpenLineOps.Operations.Metrics.Infrastructure.Persistence;

public sealed partial class SqliteOperationsMetricsStore
{
    public async ValueTask<CanonicalProductionEvent?> GetProductionEventAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return await GetImmutableAsync<CanonicalProductionEvent>(
                "operations_metric_production_events",
                "event_id",
                eventId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<PlannedProductionWindow?> GetProductionWindowAsync(
        string windowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        return await GetImmutableAsync<PlannedProductionWindow>(
                "operations_metric_planned_windows",
                "window_id",
                windowId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ShiftDefinition?> GetShiftAsync(
        string shiftId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shiftId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json, content_sha256
            FROM operations_metric_shift_definitions
            WHERE shift_id = $shift_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$shift_id", shiftId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? DeserializeAndVerify<ShiftDefinition>(
                reader.GetString(0),
                reader.GetString(1))
            : null;
    }

    public async ValueTask<DowntimeInterval?> GetDowntimeAsync(
        string downtimeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downtimeId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var facts = await ReadDowntimeFactsAsync(
                connection,
                "WHERE downtime_id = $downtime_id",
                command => command.Parameters.AddWithValue(
                    "$downtime_id",
                    downtimeId),
                cancellationToken)
            .ConfigureAwait(false);
        return facts.Count == 0 ? null : DowntimeInterval.Rehydrate(facts);
    }

    public async ValueTask<DowntimeFact?> GetDowntimeFactAsync(
        string factId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json, previous_sha256, content_sha256
            FROM operations_metric_downtime_facts
            WHERE fact_id = $fact_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$fact_id", factId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var payload = reader.GetString(0);
        var previousHash = reader.GetString(1);
        var hash = reader.GetString(2);
        if (!string.Equals(
                ComputeChainedSha256(previousHash, payload),
                hash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Persisted downtime fact hash verification failed.");
        }

        return JsonSerializer.Deserialize<DowntimeFact>(payload, JsonOptions)
               ?? throw new InvalidDataException(
                   "Persisted downtime fact is invalid.");
    }

    public async ValueTask<IReadOnlyList<CanonicalProductionEvent>>
        QueryProductionEventsAsync(
            string stationId,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default)
    {
        ValidateQuery(stationId, fromUtc, toUtc);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json, content_sha256
            FROM operations_metric_production_events
            WHERE station_id = $station_id
              AND occurred_at_utc >= $from_utc
              AND occurred_at_utc < $to_utc
            ORDER BY occurred_at_utc, received_at_utc, event_id;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
        command.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));
        return await ReadImmutableAsync<CanonicalProductionEvent>(
                command,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<PlannedProductionWindow>>
        QueryProductionWindowsAsync(
            string stationId,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default)
    {
        ValidateQuery(stationId, fromUtc, toUtc);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json, content_sha256
            FROM operations_metric_planned_windows
            WHERE station_id = $station_id
              AND starts_at_utc < $to_utc
              AND ends_at_utc > $from_utc
            ORDER BY starts_at_utc, window_id;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
        command.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));
        return await ReadImmutableAsync<PlannedProductionWindow>(
                command,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ShiftDefinition>>
        QueryShiftDefinitionsAsync(
            string stationId,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json, content_sha256
            FROM operations_metric_shift_definitions
            WHERE station_id = $station_id
            ORDER BY shift_id;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        return await ReadImmutableAsync<ShiftDefinition>(
                command,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DowntimeInterval>> QueryDowntimeAsync(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(stationId, fromUtc, toUtc);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var facts = await ReadDowntimeFactsAsync(
                connection,
                "WHERE station_id = $station_id",
                command => command.Parameters.AddWithValue(
                    "$station_id",
                    stationId),
                cancellationToken)
            .ConfigureAwait(false);
        return facts
            .GroupBy(static fact => fact.DowntimeId, StringComparer.Ordinal)
            .Select(static group => DowntimeInterval.Rehydrate(group))
            .Where(interval =>
                interval.StartedAtUtc < toUtc
                && (interval.SourceClearedAtUtc is null
                    || interval.SourceClearedAtUtc > fromUtc))
            .OrderBy(static interval => interval.StartedAtUtc)
            .ToArray();
    }

    private static async ValueTask<IReadOnlyList<T>> ReadImmutableAsync<T>(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var values = new List<T>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(DeserializeAndVerify<T>(
                reader.GetString(0),
                reader.GetString(1)));
        }

        return values;
    }

    private async ValueTask<T?> GetImmutableAsync<T>(
        string table,
        string idColumn,
        string id,
        CancellationToken cancellationToken)
    {
        var supported = (table, idColumn) is
            ("operations_metric_production_events", "event_id")
            or ("operations_metric_planned_windows", "window_id");
        if (!supported)
        {
            throw new ArgumentException("Unsupported immutable lookup.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT payload_json, content_sha256 FROM {table} WHERE {idColumn} = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? DeserializeAndVerify<T>(reader.GetString(0), reader.GetString(1))
            : default;
    }

    private static async ValueTask<IReadOnlyList<DowntimeFact>>
        ReadDowntimeFactsAsync(
            SqliteConnection connection,
            string whereClause,
            Action<SqliteCommand> addParameters,
            CancellationToken cancellationToken)
    {
        var supported = whereClause is
            "WHERE downtime_id = $downtime_id"
            or "WHERE station_id = $station_id";
        if (!supported)
        {
            throw new ArgumentException(
                "Unsupported downtime query.",
                nameof(whereClause));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT downtime_id, revision, payload_json,
                   previous_sha256, content_sha256
            FROM operations_metric_downtime_facts
            {whereClause}
            ORDER BY downtime_id, revision;
            """;
        addParameters(command);
        var facts = new List<DowntimeFact>();
        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        var revisions = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var downtimeId = reader.GetString(0);
            var revision = reader.GetInt32(1);
            var payload = reader.GetString(2);
            var previousHash = reader.GetString(3);
            var hash = reader.GetString(4);
            var expectedPrevious = heads.GetValueOrDefault(
                downtimeId,
                string.Empty);
            var expectedRevision = revisions.GetValueOrDefault(downtimeId) + 1;
            if (revision != expectedRevision
                || !string.Equals(
                    previousHash,
                    expectedPrevious,
                    StringComparison.Ordinal)
                || !string.Equals(
                    ComputeChainedSha256(previousHash, payload),
                    hash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Persisted downtime fact chain verification failed.");
            }

            var fact = JsonSerializer.Deserialize<DowntimeFact>(
                           payload,
                           JsonOptions)
                       ?? throw new InvalidDataException(
                           "Persisted downtime fact is invalid.");
            if (!string.Equals(
                    fact.DowntimeId,
                    downtimeId,
                    StringComparison.Ordinal)
                || fact.Revision != revision)
            {
                throw new InvalidDataException(
                    "Persisted downtime fact index and payload disagree.");
            }

            heads[downtimeId] = hash;
            revisions[downtimeId] = revision;
            facts.Add(fact);
        }

        return facts;
    }
}
