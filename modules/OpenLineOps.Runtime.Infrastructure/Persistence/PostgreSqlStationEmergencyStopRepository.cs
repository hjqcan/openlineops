using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using OpenLineOps.Runtime.Application.Safety;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class PostgreSqlStationEmergencyStopRepository :
    IStationEmergencyStopRepository,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = RuntimePersistenceJson.CreateOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private int _schemaCreated;

    public PostgreSqlStationEmergencyStopRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async ValueTask<StationEmergencyStopRegistration> RegisterRequestAsync(
        StationEmergencyStopRequestEvidence request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = await FindByEitherIdentityAsync(
                connection,
                transaction,
                request.IdempotencyKey,
                request.MessageId,
                lockRow: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSame(existing, request);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StationEmergencyStopRegistration(
                StationEmergencyStopRegistrationKind.Replay,
                existing);
        }

        var created = StationEmergencyStopRecordTransitions.Create(request);
        if (!await TryInsertRecordAsync(connection, transaction, created, cancellationToken)
            .ConfigureAwait(false))
        {
            existing = await FindByEitherIdentityAsync(
                    connection,
                    transaction,
                    request.IdempotencyKey,
                    request.MessageId,
                    lockRow: true,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "PostgreSQL reported an Emergency Stop identity conflict without a stored record.");
            EnsureSame(existing, request);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StationEmergencyStopRegistration(
                StationEmergencyStopRegistrationKind.Replay,
                existing);
        }

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

    public ValueTask<StationEmergencyStopRecord> RecordDispatchFailureAsync(
        string idempotencyKey,
        Guid requestMessageId,
        string failureCode,
        string failureReason,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            idempotencyKey,
            current => StationEmergencyStopRecordTransitions.DispatchFailed(
                current,
                requestMessageId,
                failureCode,
                failureReason,
                failedAtUtc),
            cancellationToken);

    public ValueTask<StationEmergencyStopRecord> RecordAcknowledgementAsync(
        StationEmergencyStopAcknowledgementEvidence acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        return TransitionAsync(
            acknowledgement.IdempotencyKey,
            current => StationEmergencyStopRecordTransitions.Acknowledge(
                current,
                acknowledgement),
            cancellationToken);
    }

    public async ValueTask<StationEmergencyStopRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_json::text FROM olo_station_emergency_stops WHERE idempotency_key = @idempotency_key LIMIT 1;";
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize(json)
            : null;
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
            SELECT document_json::text
            FROM olo_station_emergency_stops
            WHERE project_id = @project_id
              AND application_id = @application_id
              AND (@project_snapshot_id IS NULL OR project_snapshot_id = @project_snapshot_id)
              AND (@station_system_id IS NULL OR station_system_id = @station_system_id)
            ORDER BY requested_at_utc DESC, request_message_id;
            """;
        command.Parameters.AddWithValue("project_id", query.ProjectId);
        command.Parameters.AddWithValue("application_id", query.ApplicationId);
        command.Parameters.Add("project_snapshot_id", NpgsqlDbType.Text).Value =
            (object?)query.ProjectSnapshotId ?? DBNull.Value;
        command.Parameters.Add("station_system_id", NpgsqlDbType.Text).Value =
            (object?)query.StationSystemId ?? DBNull.Value;
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await GetRequiredForUpdateAsync(
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
                UPDATE olo_station_emergency_stops
                SET status = @status,
                    document_json = @document_json::jsonb,
                    last_updated_at_utc = @last_updated_at_utc
                WHERE idempotency_key = @idempotency_key;
                """;
            update.Parameters.AddWithValue("status", updated.Status.ToString());
            update.Parameters.AddWithValue("document_json", Serialize(updated));
            update.Parameters.AddWithValue("last_updated_at_utc", updated.LastUpdatedAtUtc);
            update.Parameters.AddWithValue("idempotency_key", idempotencyKey);
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

    private static async ValueTask<StationEmergencyStopRecord?> FindByEitherIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        Guid messageId,
        bool lockRow,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT document_json::text
            FROM olo_station_emergency_stops
            WHERE idempotency_key = @idempotency_key
               OR request_message_id = @request_message_id
            LIMIT 1{(lockRow ? " FOR UPDATE" : string.Empty)};
            """;
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("request_message_id", messageId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize(json)
            : null;
    }

    private static async ValueTask<StationEmergencyStopRecord> GetRequiredForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT document_json::text FROM olo_station_emergency_stops WHERE idempotency_key = @idempotency_key LIMIT 1 FOR UPDATE;";
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? Deserialize(json)
            : throw new InvalidOperationException(
                $"Emergency Stop idempotency key '{idempotencyKey}' does not exist.");
    }

    private static async ValueTask<bool> TryInsertRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StationEmergencyStopRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_station_emergency_stops (
                idempotency_key, request_message_id, project_id, application_id,
                project_snapshot_id, station_system_id, agent_id, station_id,
                status, requested_at_utc, last_updated_at_utc, document_json)
            VALUES (
                @idempotency_key, @request_message_id, @project_id, @application_id,
                @project_snapshot_id, @station_system_id, @agent_id, @station_id,
                @status, @requested_at_utc, @last_updated_at_utc, @document_json::jsonb)
            ON CONFLICT DO NOTHING;
            """;
        var request = record.Request;
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("request_message_id", request.MessageId);
        command.Parameters.AddWithValue("project_id", request.ProjectId);
        command.Parameters.AddWithValue("application_id", request.ApplicationId);
        command.Parameters.AddWithValue("project_snapshot_id", request.ProjectSnapshotId);
        command.Parameters.AddWithValue("station_system_id", request.StationSystemId);
        command.Parameters.AddWithValue("agent_id", request.AgentId);
        command.Parameters.AddWithValue("station_id", request.StationId);
        command.Parameters.AddWithValue("status", record.Status.ToString());
        command.Parameters.AddWithValue("requested_at_utc", request.RequestedAtUtc);
        command.Parameters.AddWithValue("last_updated_at_utc", record.LastUpdatedAtUtc);
        command.Parameters.AddWithValue("document_json", Serialize(record));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask InsertEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        StationSafetyEvidence evidence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO olo_station_safety_evidence (
                idempotency_key, sequence, kind, message_id, occurred_at_utc,
                failure_code, failure_reason, document_json)
            VALUES (
                @idempotency_key, @sequence, @kind, @message_id, @occurred_at_utc,
                @failure_code, @failure_reason, @document_json::jsonb);
            """;
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("sequence", evidence.Sequence);
        command.Parameters.AddWithValue("kind", evidence.Kind.ToString());
        command.Parameters.AddWithValue("message_id", evidence.MessageId);
        command.Parameters.AddWithValue("occurred_at_utc", evidence.OccurredAtUtc);
        command.Parameters.AddWithValue("failure_code", (object?)evidence.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("failure_reason", (object?)evidence.FailureReason ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "document_json",
            JsonSerializer.Serialize(evidence, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS olo_station_emergency_stops (
                    idempotency_key text PRIMARY KEY,
                    request_message_id uuid NOT NULL UNIQUE,
                    project_id text NOT NULL,
                    application_id text NOT NULL,
                    project_snapshot_id text NOT NULL,
                    station_system_id text NOT NULL,
                    agent_id text NOT NULL,
                    station_id text NOT NULL,
                    status text NOT NULL,
                    requested_at_utc timestamptz NOT NULL,
                    last_updated_at_utc timestamptz NOT NULL,
                    document_json jsonb NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_olo_station_emergency_stops_scope
                    ON olo_station_emergency_stops(
                        project_id, application_id, project_snapshot_id,
                        station_system_id, requested_at_utc DESC);

                CREATE TABLE IF NOT EXISTS olo_station_safety_evidence (
                    idempotency_key text NOT NULL REFERENCES olo_station_emergency_stops(idempotency_key) ON DELETE RESTRICT,
                    sequence bigint NOT NULL,
                    kind text NOT NULL,
                    message_id uuid NOT NULL,
                    occurred_at_utc timestamptz NOT NULL,
                    failure_code text NULL,
                    failure_reason text NULL,
                    document_json jsonb NOT NULL,
                    PRIMARY KEY(idempotency_key, sequence)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void EnsureSame(
        StationEmergencyStopRecord existing,
        StationEmergencyStopRequestEvidence request)
    {
        if (!StationSafetyCanonical.SameRequest(existing.Request, request))
        {
            throw new StationEmergencyStopIdempotencyConflictException(
                $"Emergency Stop idempotency key '{request.IdempotencyKey}' or Message ID '{request.MessageId:D}' was reused with different immutable evidence.");
        }
    }

    private static string Serialize(StationEmergencyStopRecord record) =>
        JsonSerializer.Serialize(record, JsonOptions);

    private static StationEmergencyStopRecord Deserialize(string json) =>
        JsonSerializer.Deserialize<StationEmergencyStopRecord>(json, JsonOptions)
        ?? throw new InvalidDataException("Persisted Emergency Stop evidence is empty.");

    public void Dispose() => _schemaGate.Dispose();
}
