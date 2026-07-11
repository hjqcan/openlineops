using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Safety;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteStationEmergencyStopRepository :
    IStationEmergencyStopRepository,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _schemaCreated;

    public SqliteStationEmergencyStopRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async ValueTask<StationEmergencyStopRegistration> RegisterRequestAsync(
        StationEmergencyStopRequestEvidence request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var existing = await FindByEitherIdentityAsync(
                    connection,
                    transaction,
                    request.IdempotencyKey,
                    request.MessageId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!StationSafetyCanonical.SameRequest(existing.Request, request))
                {
                    throw new StationEmergencyStopIdempotencyConflictException(
                        $"Emergency Stop idempotency key '{request.IdempotencyKey}' or Message ID '{request.MessageId:D}' was reused with different immutable evidence.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StationEmergencyStopRegistration(
                    StationEmergencyStopRegistrationKind.Replay,
                    existing);
            }

            var created = StationEmergencyStopRecordTransitions.Create(request);
            await InsertRecordAsync(connection, transaction, created, cancellationToken)
                .ConfigureAwait(false);
            await InsertEvidenceAsync(
                    connection,
                    transaction,
                    request.IdempotencyKey,
                    created.Evidence.Single(),
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StationEmergencyStopRegistration(
                StationEmergencyStopRegistrationKind.Created,
                created);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<StationEmergencyStopRecord> RecordDispatchFailureAsync(
        string idempotencyKey,
        Guid requestMessageId,
        string failureCode,
        string failureReason,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return await TransitionAsync(
                idempotencyKey,
                current => StationEmergencyStopRecordTransitions.DispatchFailed(
                    current,
                    requestMessageId,
                    failureCode,
                    failureReason,
                    failedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<StationEmergencyStopRecord> RecordAcknowledgementAsync(
        StationEmergencyStopAcknowledgementEvidence acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        return await TransitionAsync(
                acknowledgement.IdempotencyKey,
                current => StationEmergencyStopRecordTransitions.Acknowledge(
                    current,
                    acknowledgement),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<StationEmergencyStopRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_json FROM station_emergency_stops WHERE idempotency_key = $idempotency_key LIMIT 1;";
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : Deserialize(json);
    }

    public async ValueTask<IReadOnlyCollection<StationEmergencyStopRecord>> ListAsync(
        StationEmergencyStopQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM station_emergency_stops
            WHERE project_id = $project_id
              AND application_id = $application_id
              AND ($project_snapshot_id IS NULL OR project_snapshot_id = $project_snapshot_id)
              AND ($station_system_id IS NULL OR station_system_id = $station_system_id)
            ORDER BY requested_at_utc DESC, request_message_id;
            """;
        command.Parameters.AddWithValue("$project_id", query.ProjectId);
        command.Parameters.AddWithValue("$application_id", query.ApplicationId);
        command.Parameters.AddWithValue(
            "$project_snapshot_id",
            (object?)query.ProjectSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$station_system_id",
            (object?)query.StationSystemId ?? DBNull.Value);
        var records = new List<StationEmergencyStopRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(Deserialize(reader.GetString(0)));
        }

        return records;
    }

    private async ValueTask<StationEmergencyStopRecord> TransitionAsync(
        string idempotencyKey,
        Func<StationEmergencyStopRecord, StationEmergencyStopRecord> transition,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var current = await GetRequiredAsync(
                    connection,
                    transaction,
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            var updated = transition(current);
            if (updated.Evidence.Count != current.Evidence.Count)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE station_emergency_stops
                    SET status = $status,
                        document_json = $document_json,
                        last_updated_at_utc = $last_updated_at_utc
                    WHERE idempotency_key = $idempotency_key;
                    """;
                update.Parameters.AddWithValue("$status", updated.Status.ToString());
                update.Parameters.AddWithValue("$document_json", Serialize(updated));
                update.Parameters.AddWithValue(
                    "$last_updated_at_utc",
                    FormatTimestamp(updated.LastUpdatedAtUtc));
                update.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException(
                        $"Emergency Stop '{idempotencyKey}' disappeared during its audit transition.");
                }

                await InsertEvidenceAsync(
                        connection,
                        transaction,
                        idempotencyKey,
                        updated.Evidence.MaxBy(static evidence => evidence.Sequence)!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async ValueTask<StationEmergencyStopRecord?> FindByEitherIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json
            FROM station_emergency_stops
            WHERE idempotency_key = $idempotency_key
               OR request_message_id = $request_message_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("$request_message_id", messageId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize(json)
            : null;
    }

    private static async ValueTask<StationEmergencyStopRecord> GetRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT document_json FROM station_emergency_stops WHERE idempotency_key = $idempotency_key LIMIT 1;";
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize(json)
            : throw new InvalidOperationException(
                $"Emergency Stop idempotency key '{idempotencyKey}' does not exist.");
    }

    private static async ValueTask InsertRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationEmergencyStopRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_emergency_stops (
                idempotency_key, request_message_id, project_id, application_id,
                project_snapshot_id, station_system_id, agent_id, station_id,
                status, requested_at_utc, last_updated_at_utc, document_json)
            VALUES (
                $idempotency_key, $request_message_id, $project_id, $application_id,
                $project_snapshot_id, $station_system_id, $agent_id, $station_id,
                $status, $requested_at_utc, $last_updated_at_utc, $document_json);
            """;
        var request = record.Request;
        command.Parameters.AddWithValue("$idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("$request_message_id", request.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$project_id", request.ProjectId);
        command.Parameters.AddWithValue("$application_id", request.ApplicationId);
        command.Parameters.AddWithValue("$project_snapshot_id", request.ProjectSnapshotId);
        command.Parameters.AddWithValue("$station_system_id", request.StationSystemId);
        command.Parameters.AddWithValue("$agent_id", request.AgentId);
        command.Parameters.AddWithValue("$station_id", request.StationId);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$requested_at_utc", FormatTimestamp(request.RequestedAtUtc));
        command.Parameters.AddWithValue(
            "$last_updated_at_utc",
            FormatTimestamp(record.LastUpdatedAtUtc));
        command.Parameters.AddWithValue("$document_json", Serialize(record));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        StationSafetyEvidence evidence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_safety_evidence (
                idempotency_key, sequence, kind, message_id, occurred_at_utc,
                failure_code, failure_reason, document_json)
            VALUES (
                $idempotency_key, $sequence, $kind, $message_id, $occurred_at_utc,
                $failure_code, $failure_reason, $document_json);
            """;
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("$sequence", evidence.Sequence);
        command.Parameters.AddWithValue("$kind", evidence.Kind.ToString());
        command.Parameters.AddWithValue("$message_id", evidence.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(evidence.OccurredAtUtc));
        command.Parameters.AddWithValue("$failure_code", (object?)evidence.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure_reason", (object?)evidence.FailureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$document_json", JsonSerializer.Serialize(evidence, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            EnsureDatabaseDirectory();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS station_emergency_stops (
                    idempotency_key TEXT NOT NULL PRIMARY KEY,
                    request_message_id TEXT NOT NULL UNIQUE,
                    project_id TEXT NOT NULL,
                    application_id TEXT NOT NULL,
                    project_snapshot_id TEXT NOT NULL,
                    station_system_id TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    requested_at_utc TEXT NOT NULL,
                    last_updated_at_utc TEXT NOT NULL,
                    document_json TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_station_emergency_stops_scope
                    ON station_emergency_stops(
                        project_id, application_id, project_snapshot_id,
                        station_system_id, requested_at_utc);

                CREATE TABLE IF NOT EXISTS station_safety_evidence (
                    idempotency_key TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    message_id TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    failure_code TEXT NULL,
                    failure_reason TEXT NULL,
                    document_json TEXT NOT NULL,
                    PRIMARY KEY(idempotency_key, sequence),
                    FOREIGN KEY(idempotency_key)
                        REFERENCES station_emergency_stops(idempotency_key)
                        ON DELETE RESTRICT
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(builder.DataSource, ":memory:", StringComparison.Ordinal))
        {
            return;
        }

        var path = Path.GetFullPath(builder.DataSource);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string Serialize(StationEmergencyStopRecord record) =>
        JsonSerializer.Serialize(record, JsonOptions);

    private static StationEmergencyStopRecord Deserialize(string json) =>
        JsonSerializer.Deserialize<StationEmergencyStopRecord>(json, JsonOptions)
        ?? throw new InvalidDataException("Persisted Emergency Stop evidence is empty.");

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    public void Dispose() => _gate.Dispose();
}
