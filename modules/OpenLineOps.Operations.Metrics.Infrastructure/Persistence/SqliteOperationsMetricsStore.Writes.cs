using Microsoft.Data.Sqlite;
using OpenLineOps.Operations.Metrics.Application.Contracts;
using OpenLineOps.Operations.Metrics.Domain.Downtime;
using OpenLineOps.Operations.Metrics.Domain.Production;
using OpenLineOps.Operations.Metrics.Domain.Shifts;

namespace OpenLineOps.Operations.Metrics.Infrastructure.Persistence;

public sealed partial class SqliteOperationsMetricsStore
{
    public async ValueTask<OperationsMetricsWriteOutcome>
        AppendProductionEventAsync(
            CanonicalProductionEvent productionEvent,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productionEvent);
        var payload = Serialize(productionEvent);
        var hash = ComputeSha256(payload);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckImmutableIdAsync(
                    connection,
                    transaction,
                    "operations_metric_production_events",
                    "event_id",
                    productionEvent.EventId,
                    payload => ProductionSourceFactsMatch(
                        DeserializeAndVerify<CanonicalProductionEvent>(
                            payload.Payload,
                            payload.Hash),
                        productionEvent),
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return replay.Value;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO operations_metric_production_events (
                    event_id, station_id, occurred_at_utc, received_at_utc,
                    payload_json, content_sha256)
                VALUES (
                    $event_id, $station_id, $occurred_at_utc, $received_at_utc,
                    $payload_json, $content_sha256);
                """;
            command.Parameters.AddWithValue("$event_id", productionEvent.EventId);
            command.Parameters.AddWithValue("$station_id", productionEvent.StationId);
            command.Parameters.AddWithValue(
                "$occurred_at_utc",
                FormatUtc(productionEvent.OccurredAtUtc));
            command.Parameters.AddWithValue(
                "$received_at_utc",
                FormatUtc(productionEvent.ReceivedAtUtc));
            command.Parameters.AddWithValue("$payload_json", payload);
            command.Parameters.AddWithValue("$content_sha256", hash);
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return OperationsMetricsWriteOutcome.Applied;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<OperationsMetricsWriteOutcome>
        AppendShiftDefinitionAsync(
            ShiftDefinition shift,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shift);
        var payload = Serialize(shift);
        var hash = ComputeSha256(payload);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckImmutableIdAsync(
                    connection,
                    transaction,
                    "operations_metric_shift_definitions",
                    "shift_id",
                    shift.ShiftId,
                    payload => ShiftSourceFactsMatch(
                        DeserializeAndVerify<ShiftDefinition>(
                            payload.Payload,
                            payload.Hash),
                        shift),
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return replay.Value;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO operations_metric_shift_definitions (
                    shift_id, station_id, payload_json, content_sha256)
                VALUES (
                    $shift_id, $station_id, $payload_json, $content_sha256);
                """;
            command.Parameters.AddWithValue("$shift_id", shift.ShiftId);
            command.Parameters.AddWithValue("$station_id", shift.StationId);
            command.Parameters.AddWithValue("$payload_json", payload);
            command.Parameters.AddWithValue("$content_sha256", hash);
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return OperationsMetricsWriteOutcome.Applied;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<OperationsMetricsWriteOutcome>
        AppendProductionWindowAsync(
            PlannedProductionWindow window,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        var payload = Serialize(window);
        var hash = ComputeSha256(payload);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckImmutableIdAsync(
                    connection,
                    transaction,
                    "operations_metric_planned_windows",
                    "window_id",
                    window.WindowId,
                    payload => WindowSourceFactsMatch(
                        DeserializeAndVerify<PlannedProductionWindow>(
                            payload.Payload,
                            payload.Hash),
                        window),
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return replay.Value;
            }

            await EnsureNoWindowOverlapAsync(
                    connection,
                    transaction,
                    window,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO operations_metric_planned_windows (
                        window_id, shift_id, station_id, starts_at_utc,
                        ends_at_utc, payload_json, content_sha256)
                    VALUES (
                        $window_id, $shift_id, $station_id, $starts_at_utc,
                        $ends_at_utc, $payload_json, $content_sha256);
                    """;
                command.Parameters.AddWithValue("$window_id", window.WindowId);
                command.Parameters.AddWithValue("$shift_id", window.ShiftId);
                command.Parameters.AddWithValue("$station_id", window.StationId);
                command.Parameters.AddWithValue(
                    "$starts_at_utc",
                    FormatUtc(window.StartsAtUtc));
                command.Parameters.AddWithValue(
                    "$ends_at_utc",
                    FormatUtc(window.EndsAtUtc));
                command.Parameters.AddWithValue("$payload_json", payload);
                command.Parameters.AddWithValue("$content_sha256", hash);
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode == 19)
            {
                throw new OperationsMetricsConflictException(
                    "The production window conflicts with persisted shift data.");
            }

            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return OperationsMetricsWriteOutcome.Applied;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<OperationsMetricsWriteOutcome>
        AppendDowntimeFactAsync(
            DowntimeFact fact,
            int expectedRevision,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (expectedRevision < 0 || fact.Revision != expectedRevision + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "Expected revision must immediately precede the fact revision.");
        }

        var payload = Serialize(fact);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await CheckDowntimeFactReplayAsync(
                    connection,
                    transaction,
                    fact.FactId,
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return replay.Value;
            }

            var current = await GetDowntimeHeadAsync(
                    connection,
                    transaction,
                    fact.DowntimeId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new OperationsMetricsConflictException(
                    $"Downtime revision conflict. Expected {expectedRevision}, actual {current.Revision}.");
            }

            var hash = ComputeChainedSha256(current.Hash, payload);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO operations_metric_downtime_facts (
                        fact_id, downtime_id, revision, station_id, kind,
                        occurred_at_utc, payload_json, previous_sha256,
                        content_sha256)
                    VALUES (
                        $fact_id, $downtime_id, $revision, $station_id, $kind,
                        $occurred_at_utc, $payload_json, $previous_sha256,
                        $content_sha256);
                    """;
                command.Parameters.AddWithValue("$fact_id", fact.FactId);
                command.Parameters.AddWithValue("$downtime_id", fact.DowntimeId);
                command.Parameters.AddWithValue("$revision", fact.Revision);
                command.Parameters.AddWithValue("$station_id", fact.StationId);
                command.Parameters.AddWithValue("$kind", fact.Kind.ToString());
                command.Parameters.AddWithValue(
                    "$occurred_at_utc",
                    FormatUtc(fact.OccurredAtUtc));
                command.Parameters.AddWithValue("$payload_json", payload);
                command.Parameters.AddWithValue("$previous_sha256", current.Hash);
                command.Parameters.AddWithValue("$content_sha256", hash);
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode == 19)
            {
                throw new OperationsMetricsConflictException(
                    "The downtime fact conflicts with persisted history.");
            }

            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return OperationsMetricsWriteOutcome.Applied;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async ValueTask<OperationsMetricsWriteOutcome?>
        CheckImmutableIdAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            string idColumn,
            string id,
            Func<PersistedPayload, bool> matchesPayload,
            CancellationToken cancellationToken)
    {
        var supported = (table, idColumn) is
            ("operations_metric_production_events", "event_id")
            or ("operations_metric_shift_definitions", "shift_id")
            or ("operations_metric_planned_windows", "window_id");
        if (!supported)
        {
            throw new ArgumentException("Unsupported immutable fact table.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT payload_json, content_sha256 FROM {table} WHERE {idColumn} = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var payload = reader.GetString(0);
        var persistedHash = reader.GetString(1);
        if (!string.Equals(
                ComputeSha256(payload),
                persistedHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Persisted immutable fact hash verification failed.");
        }

        return matchesPayload(new PersistedPayload(payload, persistedHash))
            ? OperationsMetricsWriteOutcome.Replayed
            : throw new OperationsMetricsConflictException(
                $"Immutable fact ID '{id}' was reused with different content.");
    }

    private static async ValueTask EnsureNoWindowOverlapAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlannedProductionWindow window,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM operations_metric_planned_windows
            WHERE station_id = $station_id
              AND starts_at_utc < $ends_at_utc
              AND ends_at_utc > $starts_at_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$station_id", window.StationId);
        command.Parameters.AddWithValue(
            "$starts_at_utc",
            FormatUtc(window.StartsAtUtc));
        command.Parameters.AddWithValue(
            "$ends_at_utc",
            FormatUtc(window.EndsAtUtc));
        if (await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            throw new OperationsMetricsConflictException(
                "Planned production windows for a station cannot overlap.");
        }
    }

    private static async ValueTask<OperationsMetricsWriteOutcome?>
        CheckDowntimeFactReplayAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string factId,
            string expectedPayload,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

        return string.Equals(payload, expectedPayload, StringComparison.Ordinal)
            ? OperationsMetricsWriteOutcome.Replayed
            : throw new OperationsMetricsConflictException(
                $"Downtime fact ID '{factId}' was reused with different content.");
    }

    private static async ValueTask<(int Revision, string Hash)>
        GetDowntimeHeadAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string downtimeId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, content_sha256
            FROM operations_metric_downtime_facts
            WHERE downtime_id = $downtime_id
            ORDER BY revision DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$downtime_id", downtimeId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt32(0), reader.GetString(1))
            : (0, string.Empty);
    }

    private static bool ProductionSourceFactsMatch(
        CanonicalProductionEvent persisted,
        CanonicalProductionEvent candidate) =>
        persisted.EventId == candidate.EventId
        && persisted.StationId == candidate.StationId
        && persisted.UnitId == candidate.UnitId
        && persisted.Kind == candidate.Kind
        && persisted.SourceTimestampUtc == candidate.SourceTimestampUtc
        && persisted.OccurredAtUtc == candidate.OccurredAtUtc
        && persisted.FirstAttempt == candidate.FirstAttempt
        && persisted.Good == candidate.Good
        && persisted.CycleDuration == candidate.CycleDuration
        && persisted.SchemaVersion == candidate.SchemaVersion;

    private static bool ShiftSourceFactsMatch(
        ShiftDefinition persisted,
        ShiftDefinition candidate) =>
        persisted.ShiftId == candidate.ShiftId
        && persisted.StationId == candidate.StationId
        && persisted.Name == candidate.Name
        && persisted.TimeZoneId == candidate.TimeZoneId
        && persisted.LocalStartTime == candidate.LocalStartTime
        && persisted.LocalEndTime == candidate.LocalEndTime
        && persisted.CreatedBy == candidate.CreatedBy
        && persisted.SchemaVersion == candidate.SchemaVersion;

    private static bool WindowSourceFactsMatch(
        PlannedProductionWindow persisted,
        PlannedProductionWindow candidate) =>
        persisted.WindowId == candidate.WindowId
        && persisted.ShiftId == candidate.ShiftId
        && persisted.StationId == candidate.StationId
        && persisted.StartsAtUtc == candidate.StartsAtUtc
        && persisted.EndsAtUtc == candidate.EndsAtUtc
        && persisted.TargetQuantity == candidate.TargetQuantity
        && persisted.IdealCycleTime == candidate.IdealCycleTime
        && persisted.CreatedBy == candidate.CreatedBy
        && persisted.SchemaVersion == candidate.SchemaVersion;

    private readonly record struct PersistedPayload(
        string Payload,
        string Hash);
}
