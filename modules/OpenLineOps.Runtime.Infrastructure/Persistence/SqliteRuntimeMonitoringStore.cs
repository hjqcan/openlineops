using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed partial class SqliteRuntimeSessionRepository
{
    public async ValueTask RebuildAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await ResetMaterializedProjectionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var events = await LoadMonitoringEventsAsync(
                connection,
                transaction,
                pendingOnly: false,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var storedEvent in events)
        {
            await ProjectAsync(connection, transaction, storedEvent, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplyPendingAsync(
        IReadOnlyCollection<Guid> requiredEventIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredEventIds);
        if (requiredEventIds.Any(eventId => eventId == Guid.Empty))
        {
            throw new ArgumentException("Required Runtime monitoring event ids cannot be empty.", nameof(requiredEventIds));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await RequirePersistedEventsAsync(
                connection,
                transaction,
                requiredEventIds,
                cancellationToken)
            .ConfigureAwait(false);
        var events = await LoadMonitoringEventsAsync(
                connection,
                transaction,
                pendingOnly: true,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var storedEvent in events)
        {
            await ProjectAsync(connection, transaction, storedEvent, cancellationToken).ConfigureAwait(false);
        }

        await RequireProjectedEventsAsync(
                connection,
                transaction,
                requiredEventIds,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<RuntimeStationStatusProjection>> ListStationStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_json FROM runtime_monitoring_station_statuses;";
        return await ReadDocumentsAsync<RuntimeStationStatusProjection>(command, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<RuntimeTargetStatusProjection>> ListTargetStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_json FROM runtime_monitoring_target_statuses;";
        return await ReadDocumentsAsync<RuntimeTargetStatusProjection>(command, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<RuntimeTimelineEntry>> ListTimelineAsync(
        RuntimeSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM runtime_monitoring_timeline
            WHERE session_id = $session_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Value.ToString("D"));
        return await ReadDocumentsAsync<RuntimeTimelineEntry>(command, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<RuntimeAlarmProjection>> ListAlarmsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alarm.document_json,
                   acknowledgement.acknowledged_by,
                   acknowledgement.acknowledged_at_utc
            FROM runtime_monitoring_alarms AS alarm
            LEFT JOIN runtime_monitoring_alarm_acknowledgements AS acknowledgement
              ON acknowledgement.alarm_id = alarm.alarm_id;
            """;

        var alarms = new List<RuntimeAlarmProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var alarm = DeserializeDocument<RuntimeAlarmProjection>(reader.GetString(0));
            if (!reader.IsDBNull(1))
            {
                alarm = alarm.Acknowledge(
                    reader.GetString(1),
                    ParseTimestamp(reader.GetString(2), "alarm acknowledgement"));
            }

            alarms.Add(alarm);
        }

        return alarms;
    }

    public async ValueTask<RuntimeAlarmProjection?> AcknowledgeAlarmAsync(
        RuntimeIncidentId alarmId,
        string acknowledgedBy,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var alarm = await GetAlarmAsync(connection, transaction, alarmId, cancellationToken).ConfigureAwait(false);
        if (alarm is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO runtime_monitoring_alarm_acknowledgements (
                    alarm_id,
                    acknowledged_by,
                    acknowledged_at_utc)
                VALUES ($alarm_id, $acknowledged_by, $acknowledged_at_utc)
                ON CONFLICT(alarm_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$alarm_id", alarmId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$acknowledged_by", acknowledgedBy);
            insert.Parameters.AddWithValue("$acknowledged_at_utc", FormatTimestamp(acknowledgedAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var acknowledgement = await GetAcknowledgementAsync(
                connection,
                transaction,
                alarmId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return alarm.Acknowledge(acknowledgement.AcknowledgedBy, acknowledgement.AcknowledgedAtUtc);
    }

    private static async ValueTask AppendMonitoringEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionDocumentJson,
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken)
    {
        foreach (var domainEvent in domainEvents)
        {
            var eventDocument = RuntimeMonitoringDomainEventMapper.ToDocument(domainEvent);
            var eventDocumentJson = JsonSerializer.Serialize(eventDocument, JsonOptions);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO runtime_monitoring_events (
                    event_id,
                    session_id,
                    event_name,
                    occurred_at_utc,
                    session_document_json,
                    event_document_json,
                    projected)
                VALUES (
                    $event_id,
                    $session_id,
                    $event_name,
                    $occurred_at_utc,
                    $session_document_json,
                    $event_document_json,
                    0)
                ON CONFLICT(event_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$event_id", domainEvent.EventId.ToString("D"));
            insert.Parameters.AddWithValue("$session_id", eventDocument.SessionId.ToString("D"));
            insert.Parameters.AddWithValue("$event_name", eventDocument.EventName);
            insert.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(eventDocument.OccurredAtUtc));
            insert.Parameters.AddWithValue("$session_document_json", sessionDocumentJson);
            insert.Parameters.AddWithValue("$event_document_json", eventDocumentJson);
            var affected = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                await RequireMatchingStoredEventAsync(
                        connection,
                        transaction,
                        domainEvent.EventId,
                        eventDocument.SessionId,
                        eventDocument.EventName,
                        eventDocument.OccurredAtUtc,
                        sessionDocumentJson,
                        eventDocumentJson,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask RequireMatchingStoredEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId,
        Guid sessionId,
        string eventName,
        DateTimeOffset occurredAtUtc,
        string sessionDocumentJson,
        string eventDocumentJson,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT session_id,
                   event_name,
                   occurred_at_utc,
                   session_document_json,
                   event_document_json
            FROM runtime_monitoring_events
            WHERE event_id = $event_id;
            """;
        select.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(reader.GetString(0), sessionId.ToString("D"), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), eventName, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), FormatTimestamp(occurredAtUtc), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), sessionDocumentJson, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), eventDocumentJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Runtime monitoring event {eventId:D} conflicts with its durable record.");
        }
    }

    private static async ValueTask ResetMaterializedProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM runtime_monitoring_station_statuses;
            DELETE FROM runtime_monitoring_target_statuses;
            DELETE FROM runtime_monitoring_timeline;
            DELETE FROM runtime_monitoring_alarms;
            UPDATE runtime_monitoring_events SET projected = 0;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyCollection<StoredMonitoringEvent>> LoadMonitoringEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool pendingOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = pendingOnly
            ? """
                SELECT sequence, event_id, session_id, event_name, occurred_at_utc,
                       session_document_json, event_document_json
                FROM runtime_monitoring_events
                WHERE projected = 0
                ORDER BY sequence;
                """
            : """
                SELECT sequence, event_id, session_id, event_name, occurred_at_utc,
                       session_document_json, event_document_json
                FROM runtime_monitoring_events
                ORDER BY sequence;
                """;

        var events = new List<StoredMonitoringEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new StoredMonitoringEvent(
                reader.GetInt64(0),
                Guid.ParseExact(reader.GetString(1), "D"),
                Guid.ParseExact(reader.GetString(2), "D"),
                reader.GetString(3),
                ParseTimestamp(reader.GetString(4), "monitoring event"),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return events;
    }

    private static async ValueTask ProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMonitoringEvent storedEvent,
        CancellationToken cancellationToken)
    {
        var session = DeserializeSession(storedEvent.SessionDocumentJson);
        var eventDocument = JsonSerializer.Deserialize<PersistedRuntimeMonitoringEvent>(
                storedEvent.EventDocumentJson,
                JsonOptions)
            ?? throw new InvalidDataException(
                $"Persisted Runtime monitoring event {storedEvent.EventId:D} document is empty.");
        if (eventDocument.EventId != storedEvent.EventId
            || eventDocument.SessionId != storedEvent.SessionId
            || !string.Equals(eventDocument.EventName, storedEvent.EventName, StringComparison.Ordinal)
            || eventDocument.OccurredAtUtc != storedEvent.OccurredAtUtc)
        {
            throw new InvalidDataException(
                $"Persisted Runtime monitoring event {storedEvent.EventId:D} metadata is inconsistent.");
        }

        var domainEvent = RuntimeMonitoringDomainEventMapper.ToDomainEvent(eventDocument);
        var projection = RuntimeMonitoringEventProjection.Create(storedEvent.Sequence, session, domainEvent);
        await UpsertStationStatusAsync(connection, transaction, projection, cancellationToken).ConfigureAwait(false);
        if (projection.ResetStationTargets)
        {
            await DeleteStationTargetsAsync(connection, transaction, projection.StationStatus, cancellationToken)
                .ConfigureAwait(false);
        }

        await InsertTimelineEntryAsync(connection, transaction, projection.TimelineEntry, cancellationToken)
            .ConfigureAwait(false);
        if (projection.TargetStatus is not null)
        {
            await UpsertTargetStatusAsync(
                    connection,
                    transaction,
                    projection.Sequence,
                    projection.TargetStatus,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (projection.Alarm is not null)
        {
            await InsertAlarmAsync(
                    connection,
                    transaction,
                    projection.Sequence,
                    projection.Alarm,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await MarkProjectedAsync(connection, transaction, storedEvent.EventId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask UpsertStationStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RuntimeMonitoringEventProjection projection,
        CancellationToken cancellationToken)
    {
        var status = projection.StationStatus;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runtime_monitoring_station_statuses (
                project_id, application_id, project_snapshot_id, topology_id,
                production_run_id, operation_id, station_system_id,
                last_event_sequence, document_json)
            VALUES (
                $project_id, $application_id, $project_snapshot_id, $topology_id,
                $production_run_id, $operation_id, $station_system_id,
                $last_event_sequence, $document_json)
            ON CONFLICT (
                project_id, application_id, project_snapshot_id, topology_id,
                production_run_id, operation_id, station_system_id)
            DO UPDATE SET
                last_event_sequence = excluded.last_event_sequence,
                document_json = excluded.document_json
            WHERE excluded.last_event_sequence > runtime_monitoring_station_statuses.last_event_sequence;
            """;
        AddStationKeyParameters(command, status);
        command.Parameters.AddWithValue("$last_event_sequence", projection.Sequence);
        command.Parameters.AddWithValue("$document_json", SerializeDocument(status));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask DeleteStationTargetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RuntimeStationStatusProjection stationStatus,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM runtime_monitoring_target_statuses
            WHERE project_id = $project_id
              AND application_id = $application_id
              AND project_snapshot_id = $project_snapshot_id
              AND topology_id = $topology_id
              AND production_run_id = $production_run_id
              AND operation_id = $operation_id
              AND station_system_id = $station_system_id;
            """;
        AddStationKeyParameters(command, stationStatus);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertTimelineEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RuntimeTimelineEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runtime_monitoring_timeline (sequence, event_id, session_id, document_json)
            VALUES ($sequence, $event_id, $session_id, $document_json)
            ON CONFLICT(event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$sequence", entry.Sequence);
        command.Parameters.AddWithValue("$event_id", entry.EventId.ToString("D"));
        command.Parameters.AddWithValue("$session_id", entry.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$document_json", SerializeDocument(entry));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpsertTargetStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sequence,
        RuntimeTargetStatusProjection status,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runtime_monitoring_target_statuses (
                project_id, application_id, project_snapshot_id, topology_id,
                production_run_id, operation_id, station_system_id, target_kind, target_id,
                last_event_sequence, document_json)
            VALUES (
                $project_id, $application_id, $project_snapshot_id, $topology_id,
                $production_run_id, $operation_id, $station_system_id, $target_kind, $target_id,
                $last_event_sequence, $document_json)
            ON CONFLICT (
                project_id, application_id, project_snapshot_id, topology_id,
                production_run_id, operation_id, station_system_id, target_kind, target_id)
            DO UPDATE SET
                last_event_sequence = excluded.last_event_sequence,
                document_json = excluded.document_json
            WHERE excluded.last_event_sequence > runtime_monitoring_target_statuses.last_event_sequence;
            """;
        AddTargetKeyParameters(command, status);
        command.Parameters.AddWithValue("$last_event_sequence", sequence);
        command.Parameters.AddWithValue("$document_json", SerializeDocument(status));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertAlarmAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sequence,
        RuntimeAlarmProjection alarm,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runtime_monitoring_alarms (
                alarm_id, station_system_id, occurred_at_utc, last_event_sequence, document_json)
            VALUES ($alarm_id, $station_system_id, $occurred_at_utc, $last_event_sequence, $document_json)
            ON CONFLICT(alarm_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$alarm_id", alarm.AlarmId.Value.ToString("D"));
        command.Parameters.AddWithValue("$station_system_id", alarm.StationSystemId);
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(alarm.OccurredAtUtc));
        command.Parameters.AddWithValue("$last_event_sequence", sequence);
        command.Parameters.AddWithValue("$document_json", SerializeDocument(alarm));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Runtime alarm {alarm.AlarmId} conflicts with its projected record.");
        }
    }

    private static async ValueTask MarkProjectedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE runtime_monitoring_events
            SET projected = 1
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Runtime monitoring event {eventId:D} could not be marked as projected.");
        }
    }

    private static async ValueTask RequirePersistedEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<Guid> requiredEventIds,
        CancellationToken cancellationToken)
    {
        foreach (var eventId in requiredEventIds.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM runtime_monitoring_events WHERE event_id = $event_id;";
            command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
            var count = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (count != 1)
            {
                throw new InvalidOperationException(
                    $"Runtime monitoring event {eventId:D} was published before durable session persistence.");
            }
        }
    }

    private static async ValueTask RequireProjectedEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<Guid> requiredEventIds,
        CancellationToken cancellationToken)
    {
        foreach (var eventId in requiredEventIds.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT projected
                FROM runtime_monitoring_events
                WHERE event_id = $event_id;
                """;
            command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
            var projected = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (projected != 1)
            {
                throw new InvalidOperationException(
                    $"Runtime monitoring event {eventId:D} was not durably projected.");
            }
        }
    }

    private static async ValueTask<IReadOnlyCollection<TDocument>> ReadDocumentsAsync<TDocument>(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var documents = new List<TDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(DeserializeDocument<TDocument>(reader.GetString(0)));
        }

        return documents;
    }

    private static async ValueTask<RuntimeAlarmProjection?> GetAlarmAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RuntimeIncidentId alarmId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT document_json FROM runtime_monitoring_alarms WHERE alarm_id = $alarm_id;";
        command.Parameters.AddWithValue("$alarm_id", alarmId.Value.ToString("D"));
        var document = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : DeserializeDocument<RuntimeAlarmProjection>((string)document);
    }

    private static async ValueTask<AlarmAcknowledgement> GetAcknowledgementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RuntimeIncidentId alarmId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT acknowledged_by, acknowledged_at_utc
            FROM runtime_monitoring_alarm_acknowledgements
            WHERE alarm_id = $alarm_id;
            """;
        command.Parameters.AddWithValue("$alarm_id", alarmId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Runtime alarm {alarmId.Value:D} acknowledgement was not persisted.");
        }

        return new AlarmAcknowledgement(
            reader.GetString(0),
            ParseTimestamp(reader.GetString(1), "alarm acknowledgement"));
    }

    private static void AddStationKeyParameters(
        SqliteCommand command,
        RuntimeStationStatusProjection status)
    {
        command.Parameters.AddWithValue("$project_id", status.ProjectId);
        command.Parameters.AddWithValue("$application_id", status.ApplicationId);
        command.Parameters.AddWithValue("$project_snapshot_id", status.ProjectSnapshotId);
        command.Parameters.AddWithValue("$topology_id", status.TopologyId);
        command.Parameters.AddWithValue("$production_run_id", status.ProductionRunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_id", status.OperationId);
        command.Parameters.AddWithValue("$station_system_id", status.StationSystemId);
    }

    private static void AddTargetKeyParameters(
        SqliteCommand command,
        RuntimeTargetStatusProjection status)
    {
        command.Parameters.AddWithValue("$project_id", status.ProjectId);
        command.Parameters.AddWithValue("$application_id", status.ApplicationId);
        command.Parameters.AddWithValue("$project_snapshot_id", status.ProjectSnapshotId);
        command.Parameters.AddWithValue("$topology_id", status.TopologyId);
        command.Parameters.AddWithValue("$production_run_id", status.ProductionRunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_id", status.OperationId);
        command.Parameters.AddWithValue("$station_system_id", status.StationSystemId);
        command.Parameters.AddWithValue("$target_kind", status.TargetKind);
        command.Parameters.AddWithValue("$target_id", status.TargetId);
    }

    private static string SerializeDocument<TDocument>(TDocument document)
    {
        return JsonSerializer.Serialize(document, MonitoringJsonOptions);
    }

    private static TDocument DeserializeDocument<TDocument>(string documentJson)
    {
        return JsonSerializer.Deserialize<TDocument>(documentJson, MonitoringJsonOptions)
            ?? throw new InvalidDataException(
                $"Persisted Runtime monitoring {typeof(TDocument).Name} document is empty.");
    }

    private static DateTimeOffset ParseTimestamp(string value, string owner)
    {
        if (DateTimeOffset.TryParseExact(
                value,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return timestamp;
        }

        throw new InvalidDataException($"Persisted Runtime {owner} timestamp '{value}' is invalid.");
    }

    private static void RequireRuntimeSessionEvents(
        RuntimeSession session,
        IReadOnlyCollection<IDomainEvent> domainEvents)
    {
        var pendingEventIds = session.DomainEvents.Select(domainEvent => domainEvent.EventId).ToArray();
        if (!pendingEventIds.SequenceEqual(domainEvents.Select(domainEvent => domainEvent.EventId)))
        {
            throw new InvalidOperationException(
                "Runtime session persistence must atomically include every pending Domain Event in order.");
        }

        if (domainEvents.Select(domainEvent => domainEvent.EventId).Distinct().Count() != domainEvents.Count)
        {
            throw new InvalidOperationException("Runtime session persistence contains duplicate Domain Event ids.");
        }

        foreach (var domainEvent in domainEvents)
        {
            _ = RuntimeMonitoringEventProjection.Create(1, session, domainEvent);
        }
    }

    private sealed record StoredMonitoringEvent(
        long Sequence,
        Guid EventId,
        Guid SessionId,
        string EventName,
        DateTimeOffset OccurredAtUtc,
        string SessionDocumentJson,
        string EventDocumentJson);

    private sealed record AlarmAcknowledgement(
        string AcknowledgedBy,
        DateTimeOffset AcknowledgedAtUtc);
}
