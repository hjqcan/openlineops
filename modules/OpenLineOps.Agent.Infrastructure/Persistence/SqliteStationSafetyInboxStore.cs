using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class SqliteStationSafetyInboxStore : IStationSafetyInboxStore, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationSafetyInboxStore(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<StationSafetyInboxEntry?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT command_kind,
                   request_sha256,
                   request_message_id,
                   acknowledgement_message_id,
                   target_job_id,
                   agent_id,
                   station_id,
                   received_at_utc,
                   acknowledgement_json,
                   completed_at_utc
            FROM station_safety_inbox
            WHERE idempotency_key = $idempotency_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEntry(idempotencyKey, reader)
            : null;
    }

    public async ValueTask<StationSafetyInboxEntry?> GetJobCancellationAsync(
        OpenLineOps.Agent.Domain.StationJobs.StationJobId jobId,
        CancellationToken cancellationToken = default)
    {
        if (jobId.Value == Guid.Empty)
        {
            throw new ArgumentException("Station job id cannot be empty.", nameof(jobId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT idempotency_key,
                   command_kind,
                   request_sha256,
                   request_message_id,
                   acknowledgement_message_id,
                   target_job_id,
                   agent_id,
                   station_id,
                   received_at_utc,
                   acknowledgement_json,
                   completed_at_utc
            FROM station_safety_inbox
            WHERE command_kind = $command_kind
              AND target_job_id = $target_job_id
            ORDER BY received_at_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$command_kind", StationSafetyCommandKind.JobCancel.ToString());
        command.Parameters.AddWithValue("$target_job_id", jobId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEntry(reader.GetString(0), reader, 1)
            : null;
    }

    public async ValueTask<bool> TryBeginAsync(
        StationSafetyInboxEntry entry,
        CancellationToken cancellationToken = default)
    {
        ValidatePendingEntry(entry);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO station_safety_inbox (
                idempotency_key,
                command_kind,
                request_sha256,
                request_message_id,
                acknowledgement_message_id,
                target_job_id,
                agent_id,
                station_id,
                received_at_utc,
                acknowledgement_json,
                completed_at_utc)
            VALUES (
                $idempotency_key,
                $command_kind,
                $request_sha256,
                $request_message_id,
                $acknowledgement_message_id,
                $target_job_id,
                $agent_id,
                $station_id,
                $received_at_utc,
                NULL,
                NULL)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$idempotency_key", entry.IdempotencyKey);
        command.Parameters.AddWithValue("$command_kind", entry.CommandKind.ToString());
        command.Parameters.AddWithValue("$request_sha256", entry.RequestSha256);
        command.Parameters.AddWithValue("$request_message_id", entry.RequestMessageId.ToString("D"));
        command.Parameters.AddWithValue(
            "$acknowledgement_message_id",
            entry.AcknowledgementMessageId.ToString("D"));
        command.Parameters.AddWithValue(
            "$target_job_id",
            entry.TargetJobId is null ? DBNull.Value : entry.TargetJobId.Value.ToString("D"));
        command.Parameters.AddWithValue("$agent_id", entry.AgentId);
        command.Parameters.AddWithValue("$station_id", entry.StationId);
        command.Parameters.AddWithValue("$received_at_utc", FormatUtc(entry.ReceivedAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<StationSafetyInboxEntry> CompleteAsync(
        string idempotencyKey,
        StationSafetyCommandKind commandKind,
        string requestSha256,
        string acknowledgementJson,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (!Enum.IsDefined(commandKind))
        {
            throw new ArgumentOutOfRangeException(nameof(commandKind));
        }

        ValidateSha256(requestSha256, nameof(requestSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(acknowledgementJson);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE station_safety_inbox
                SET acknowledgement_json = $acknowledgement_json,
                    completed_at_utc = $completed_at_utc
                WHERE idempotency_key = $idempotency_key
                  AND command_kind = $command_kind
                  AND request_sha256 = $request_sha256
                  AND acknowledgement_json IS NULL;
                """;
            command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
            command.Parameters.AddWithValue("$command_kind", commandKind.ToString());
            command.Parameters.AddWithValue("$request_sha256", requestSha256);
            command.Parameters.AddWithValue("$acknowledgement_json", acknowledgementJson);
            command.Parameters.AddWithValue("$completed_at_utc", FormatUtc(completedAtUtc));
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var completed = await GetAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Safety Inbox entry '{idempotencyKey}' does not exist.");
        if (completed.CommandKind != commandKind
            || !string.Equals(completed.RequestSha256, requestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Safety command idempotency key '{idempotencyKey}' was reused with different evidence.");
        }

        return completed.AcknowledgementJson is null
            ? throw new InvalidOperationException(
                $"Safety Inbox entry '{idempotencyKey}' could not be completed.")
            : completed;
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
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;

                CREATE TABLE IF NOT EXISTS station_safety_inbox (
                    idempotency_key TEXT NOT NULL PRIMARY KEY,
                    command_kind TEXT NOT NULL,
                    request_sha256 TEXT NOT NULL,
                    request_message_id TEXT NOT NULL,
                    acknowledgement_message_id TEXT NOT NULL,
                    target_job_id TEXT NULL,
                    agent_id TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    acknowledgement_json TEXT NULL,
                    completed_at_utc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_station_safety_inbox_completion
                    ON station_safety_inbox(completed_at_utc, received_at_utc);

                CREATE INDEX IF NOT EXISTS ix_station_safety_inbox_job_cancel
                    ON station_safety_inbox(target_job_id, received_at_utc)
                    WHERE command_kind = 'JobCancel';
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static StationSafetyInboxEntry ReadEntry(
        string idempotencyKey,
        SqliteDataReader reader,
        int offset = 0)
    {
        return new StationSafetyInboxEntry(
            idempotencyKey,
            ParseCommandKind(reader.GetString(offset)),
            reader.GetString(offset + 1),
            Guid.ParseExact(reader.GetString(offset + 2), "D"),
            Guid.ParseExact(reader.GetString(offset + 3), "D"),
            reader.IsDBNull(offset + 4)
                ? null
                : Guid.ParseExact(reader.GetString(offset + 4), "D"),
            reader.GetString(offset + 5),
            reader.GetString(offset + 6),
            ParseUtc(reader.GetString(offset + 7)),
            reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
            reader.IsDBNull(offset + 9) ? null : ParseUtc(reader.GetString(offset + 9)));
    }

    private static StationSafetyCommandKind ParseCommandKind(string value)
    {
        return Enum.TryParse<StationSafetyCommandKind>(value, ignoreCase: false, out var parsed)
               && Enum.IsDefined(parsed)
               && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException(
                $"Persisted safety command kind '{value}' is invalid.");
    }

    private static void ValidatePendingEntry(StationSafetyInboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.IdempotencyKey);
        if (!Enum.IsDefined(entry.CommandKind)
            || entry.RequestMessageId == Guid.Empty
            || entry.AcknowledgementMessageId == Guid.Empty)
        {
            throw new ArgumentException("Safety Inbox identities are invalid.", nameof(entry));
        }

        if ((entry.CommandKind == StationSafetyCommandKind.JobCancel) != entry.TargetJobId.HasValue
            || entry.TargetJobId == Guid.Empty)
        {
            throw new ArgumentException(
                "Only a job cancellation Inbox entry requires a non-empty target job id.",
                nameof(entry));
        }

        ValidateSha256(entry.RequestSha256, nameof(entry.RequestSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.StationId);
        if (entry.AcknowledgementJson is not null || entry.CompletedAtUtc is not null)
        {
            throw new ArgumentException("A new safety Inbox entry must be pending.", nameof(entry));
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("SHA-256 must be lowercase hexadecimal.", parameterName);
        }
    }

    private static string RequireFileBackedConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new ArgumentException(
                "Station Agent safety Inbox requires a file-backed SQLite data source.",
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

    public void Dispose() => _schemaLock.Dispose();
}
