using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class SqliteStationJobStore : IStationJobStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        StationJobPersistenceJson.CreateOptions();

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationJobStore(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<StationJobPersistenceEntry?> GetAsync(
        StationJobId jobId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM station_jobs
            WHERE job_id = $job_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$job_id", jobId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? DeserializeEntry(reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    public async ValueTask<StationJobPersistenceEntry?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM station_jobs
            WHERE idempotency_key = $idempotency_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? DeserializeEntry(reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    public async ValueTask<bool> TryAddAsync(
        StationJob job,
        Guid inboundMessageId,
        IReadOnlyCollection<StationJobOutboxMessage> outboxMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outboxMessages);
        if (inboundMessageId == Guid.Empty)
        {
            throw new ArgumentException("Inbound message id cannot be empty.", nameof(inboundMessageId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await TryRecordInboxAsync(
                connection,
                transaction,
                inboundMessageId,
                job.Id,
                job.RequestedAtUtc,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO station_jobs (
                    job_id,
                    idempotency_key,
                    station_id,
                    status,
                    document_json,
                    revision,
                    requested_at_utc,
                    updated_at_utc)
                VALUES (
                    $job_id,
                    $idempotency_key,
                    $station_id,
                    $status,
                    $document_json,
                    0,
                    $requested_at_utc,
                    $updated_at_utc)
                ON CONFLICT DO NOTHING;
                """;
            AddJobParameters(command, job);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await InsertOutboxAsync(connection, transaction, outboxMessages, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<long> SaveAsync(
        StationJob job,
        long expectedRevision,
        IReadOnlyCollection<StationJobOutboxMessage> outboxMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outboxMessages);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var nextRevision = checked(expectedRevision + 1);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE station_jobs
                SET status = $status,
                    document_json = $document_json,
                    revision = $next_revision,
                    updated_at_utc = $updated_at_utc
                WHERE job_id = $job_id
                  AND revision = $expected_revision;
                """;
            AddJobParameters(command, job);
            command.Parameters.AddWithValue("$expected_revision", expectedRevision);
            command.Parameters.AddWithValue("$next_revision", nextRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new StationJobConcurrencyException(job.Id, expectedRevision);
            }
        }

        await InsertOutboxAsync(connection, transaction, outboxMessages, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return nextRevision;
    }

    public async ValueTask<IReadOnlyCollection<StationJobPersistenceEntry>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json, revision
            FROM station_jobs
            WHERE status IN ('Accepted', 'Running', 'RecoveryRequired')
            ORDER BY requested_at_utc, job_id;
            """;
        var result = new List<StationJobPersistenceEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(DeserializeEntry(reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    public async ValueTask<IReadOnlyCollection<StationJobOutboxMessage>> ListPendingOutboxAsync(
        int maximumCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id,
                   job_id,
                   sequence,
                   kind,
                   payload_json,
                   created_at_utc,
                   attempt_count,
                   next_attempt_at_utc,
                   acknowledged_at_utc
            FROM station_job_outbox
            WHERE acknowledged_at_utc IS NULL
              AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now_utc)
            ORDER BY created_at_utc, job_id, sequence, message_id
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$now_utc", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var result = new List<StationJobOutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StationJobOutboxMessage(
                Guid.Parse(reader.GetString(0)),
                new StationJobId(Guid.Parse(reader.GetString(1))),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseUtc(reader.GetString(5)),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8))));
        }

        return result;
    }

    public async ValueTask AcknowledgeOutboxAsync(
        Guid messageId,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await UpdateOutboxAsync(
                messageId,
                "acknowledged_at_utc = $timestamp",
                acknowledgedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RecordOutboxFailureAsync(
        Guid messageId,
        DateTimeOffset retryAtUtc,
        CancellationToken cancellationToken = default)
    {
        await UpdateOutboxAsync(
                messageId,
                "attempt_count = attempt_count + 1, next_attempt_at_utc = $timestamp",
                retryAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }

    private async ValueTask UpdateOutboxAsync(
        Guid messageId,
        string updateSql,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("Outbox message id cannot be empty.", nameof(messageId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE station_job_outbox
            SET {updateSql}
            WHERE message_id = $message_id;
            """;
        command.Parameters.AddWithValue("$message_id", messageId.ToString("D"));
        command.Parameters.AddWithValue("$timestamp", FormatUtc(timestamp));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Station job outbox message {messageId:D} does not exist.");
        }
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaCreated == 1)
            {
                return;
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS station_jobs (
                    job_id TEXT PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    station_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    document_json TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    requested_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS station_job_inbox (
                    inbound_message_id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    FOREIGN KEY(job_id) REFERENCES station_jobs(job_id)
                        DEFERRABLE INITIALLY DEFERRED
                );

                CREATE TABLE IF NOT EXISTS station_job_outbox (
                    message_id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    next_attempt_at_utc TEXT NULL,
                    acknowledged_at_utc TEXT NULL,
                    UNIQUE(job_id, sequence),
                    FOREIGN KEY(job_id) REFERENCES station_jobs(job_id)
                );

                CREATE INDEX IF NOT EXISTS ix_station_jobs_recovery
                    ON station_jobs(status, requested_at_utc);
                CREATE INDEX IF NOT EXISTS ix_station_job_outbox_pending
                    ON station_job_outbox(acknowledged_at_utc, next_attempt_at_utc, created_at_utc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask<bool> TryRecordInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid inboundMessageId,
        StationJobId jobId,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_job_inbox (inbound_message_id, job_id, received_at_utc)
            VALUES ($message_id, $job_id, $received_at_utc)
            ON CONFLICT(inbound_message_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$message_id", inboundMessageId.ToString("D"));
        command.Parameters.AddWithValue("$job_id", jobId.Value.ToString("D"));
        command.Parameters.AddWithValue("$received_at_utc", FormatUtc(receivedAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask InsertOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<StationJobOutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO station_job_outbox (
                    message_id,
                    job_id,
                    sequence,
                    kind,
                    payload_json,
                    created_at_utc,
                    attempt_count,
                    next_attempt_at_utc,
                    acknowledged_at_utc)
                VALUES (
                    $message_id,
                    $job_id,
                    $sequence,
                    $kind,
                    $payload_json,
                    $created_at_utc,
                    $attempt_count,
                    $next_attempt_at_utc,
                    $acknowledged_at_utc);
                """;
            command.Parameters.AddWithValue("$message_id", message.MessageId.ToString("D"));
            command.Parameters.AddWithValue("$job_id", message.JobId.Value.ToString("D"));
            command.Parameters.AddWithValue("$sequence", message.Sequence);
            command.Parameters.AddWithValue("$kind", message.Kind);
            command.Parameters.AddWithValue("$payload_json", message.PayloadJson);
            command.Parameters.AddWithValue("$created_at_utc", FormatUtc(message.CreatedAtUtc));
            command.Parameters.AddWithValue("$attempt_count", message.AttemptCount);
            command.Parameters.AddWithValue(
                "$next_attempt_at_utc",
                message.NextAttemptAtUtc is null ? DBNull.Value : FormatUtc(message.NextAttemptAtUtc.Value));
            command.Parameters.AddWithValue(
                "$acknowledged_at_utc",
                message.AcknowledgedAtUtc is null ? DBNull.Value : FormatUtc(message.AcknowledgedAtUtc.Value));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddJobParameters(SqliteCommand command, StationJob job)
    {
        var now = job.CompletedAtUtc
            ?? job.LastProgressAtUtc
            ?? job.StartedAtUtc
            ?? job.AcceptedAtUtc
            ?? job.RequestedAtUtc;
        command.Parameters.AddWithValue("$job_id", job.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$idempotency_key", job.IdempotencyKey);
        command.Parameters.AddWithValue("$station_id", job.StationId);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue(
            "$document_json",
            JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions));
        command.Parameters.AddWithValue("$requested_at_utc", FormatUtc(job.RequestedAtUtc));
        command.Parameters.AddWithValue("$updated_at_utc", FormatUtc(now));
    }

    private static StationJobPersistenceEntry DeserializeEntry(string json, long revision)
    {
        var snapshot = JsonSerializer.Deserialize<StationJobSnapshot>(json, JsonOptions)
            ?? throw new InvalidDataException("Station job document is null.");
        return new StationJobPersistenceEntry(
            StationJob.Restore(snapshot).ToSnapshot(),
            revision);
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static string RequireFileBackedConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "Station Agent SQLite requires a file-backed data source.",
                nameof(connectionString));
        }

        var fullPath = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        builder.DataSource = fullPath;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        return builder.ToString();
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
}
