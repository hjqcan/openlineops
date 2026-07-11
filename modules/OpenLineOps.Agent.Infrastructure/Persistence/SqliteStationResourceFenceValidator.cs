using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class SqliteStationResourceFenceValidator :
    IStationResourceFenceValidator,
    IStationResourceLeaseChangeInbox,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _connectionString;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _validationLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationResourceFenceValidator(string connectionString, IClock clock)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<StationResourceFenceValidationResult> ValidateAndAdvanceAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var nowUtc = _clock.UtcNow;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Agent clock must use UTC offset zero.");
        }

        if (job.ResourceFences
                .Select(static fence => (fence.ResourceKind, fence.ResourceId))
                .Distinct()
                .Count() != job.ResourceFences.Count)
        {
            return StationResourceFenceValidationResult.Reject(
                "Station job contains duplicate resource fences.");
        }

        var expired = job.ResourceFences.FirstOrDefault(fence =>
            fence.ExpiresAtUtc.Offset != TimeSpan.Zero || fence.ExpiresAtUtc <= nowUtc);
        if (expired is not null)
        {
            return StationResourceFenceValidationResult.Reject(
                $"Resource {expired.ResourceKind}/{expired.ResourceId} fence expired before hardware start.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _validationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var fence in job.ResourceFences
                         .OrderBy(fence => fence.ResourceKind, StringComparer.Ordinal)
                         .ThenBy(fence => fence.ResourceId, StringComparer.Ordinal))
            {
                var current = await GetCurrentAsync(connection, transaction, fence, cancellationToken)
                    .ConfigureAwait(false);
                if (current is not null
                    && (current.FencingToken > fence.FencingToken
                        || (current.FencingToken == fence.FencingToken && current.JobId != job.JobId)))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return StationResourceFenceValidationResult.Reject(
                        $"Resource {fence.ResourceKind}/{fence.ResourceId} has fencing token "
                        + $"{current.FencingToken}; job token {fence.FencingToken} is stale.");
                }
            }

            foreach (var fence in job.ResourceFences)
            {
                await UpsertAsync(connection, transaction, job.JobId, fence, cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StationResourceFenceValidationResult.Accept();
        }
        finally
        {
            _validationLock.Release();
        }
    }

    public async ValueTask ApplyAsync(
        ResourceLeaseChanged change,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(change);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _validationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(change, JsonOptions);
            await using (var existingCommand = connection.CreateCommand())
            {
                existingCommand.Transaction = transaction;
                existingCommand.CommandText = """
                    SELECT message_id, idempotency_key, payload_json
                    FROM station_resource_lease_inbox
                    WHERE message_id = $message_id OR idempotency_key = $idempotency_key
                    LIMIT 1;
                    """;
                existingCommand.Parameters.AddWithValue("$message_id", change.MessageId.ToString("D"));
                existingCommand.Parameters.AddWithValue("$idempotency_key", change.IdempotencyKey);
                await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var sameIdentity = string.Equals(
                            reader.GetString(0),
                            change.MessageId.ToString("D"),
                            StringComparison.Ordinal)
                        && string.Equals(
                            reader.GetString(1),
                            change.IdempotencyKey,
                            StringComparison.Ordinal);
                    using var existingJson = JsonDocument.Parse(reader.GetString(2));
                    using var candidateJson = JsonDocument.Parse(payload);
                    if (!sameIdentity
                        || !JsonElement.DeepEquals(
                            existingJson.RootElement,
                            candidateJson.RootElement))
                    {
                        throw new InvalidOperationException(
                            "Resource lease Inbox identity was reused with different evidence.");
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            var evidence = new StationResourceFenceEvidence(
                change.ResourceKind,
                change.ResourceId,
                change.FencingToken,
                change.ExpiresAtUtc);
            var current = await GetCurrentAsync(
                    connection,
                    transaction,
                    evidence,
                    cancellationToken)
                .ConfigureAwait(false);
            var jobId = new StationJobId(change.JobId);
            if (current is not null
                && (current.FencingToken > change.FencingToken
                    || (current.FencingToken == change.FencingToken
                        && (current.JobId != jobId
                            || current.ExpiresAtUtc != change.ExpiresAtUtc))))
            {
                throw new InvalidOperationException(
                    $"Resource {change.ResourceKind}/{change.ResourceId} lease change is stale or conflicts with its current owner.");
            }

            await UpsertAsync(connection, transaction, jobId, evidence, cancellationToken)
                .ConfigureAwait(false);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO station_resource_lease_inbox (
                        message_id, idempotency_key, payload_json, received_at_utc)
                    VALUES ($message_id, $idempotency_key, $payload_json, $received_at_utc);
                    """;
                insert.Parameters.AddWithValue("$message_id", change.MessageId.ToString("D"));
                insert.Parameters.AddWithValue("$idempotency_key", change.IdempotencyKey);
                insert.Parameters.AddWithValue("$payload_json", payload);
                insert.Parameters.AddWithValue(
                    "$received_at_utc",
                    change.ChangedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _validationLock.Release();
        }
    }

    public async ValueTask<StationResourceFenceValidationResult> ValidateCurrentAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var nowUtc = _clock.UtcNow;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Agent clock must use UTC offset zero.");
        }

        if (job.ResourceFences
                .Select(static fence => (fence.ResourceKind, fence.ResourceId))
                .Distinct()
                .Count() != job.ResourceFences.Count)
        {
            return StationResourceFenceValidationResult.Reject(
                "Station job contains duplicate resource fences.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _validationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var fence in job.ResourceFences
                         .OrderBy(fence => fence.ResourceKind, StringComparer.Ordinal)
                         .ThenBy(fence => fence.ResourceId, StringComparer.Ordinal))
            {
                var current = await GetCurrentAsync(connection, transaction, fence, cancellationToken)
                    .ConfigureAwait(false);
                if (current is null
                    || current.FencingToken != fence.FencingToken
                    || current.JobId != job.JobId
                    || current.ExpiresAtUtc != fence.ExpiresAtUtc
                    || current.ExpiresAtUtc <= nowUtc)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return StationResourceFenceValidationResult.Reject(
                        $"Resource {fence.ResourceKind}/{fence.ResourceId} token {fence.FencingToken} is no longer current for job {job.JobId}.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StationResourceFenceValidationResult.Accept();
        }
        finally
        {
            _validationLock.Release();
        }
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
        _validationLock.Dispose();
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

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;

                CREATE TABLE IF NOT EXISTS station_resource_fences (
                    resource_kind TEXT NOT NULL,
                    resource_id TEXT NOT NULL,
                    fencing_token INTEGER NOT NULL,
                    owner_job_id TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    PRIMARY KEY(resource_kind, resource_id)
                );
                CREATE TABLE IF NOT EXISTS station_resource_lease_inbox (
                    message_id TEXT PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    payload_json TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask<CurrentFence?> GetCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationResourceFenceEvidence fence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fencing_token, owner_job_id, expires_at_utc
            FROM station_resource_fences
            WHERE resource_kind = $resource_kind
              AND resource_id = $resource_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$resource_kind", fence.ResourceKind);
        command.Parameters.AddWithValue("$resource_id", fence.ResourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CurrentFence(
                reader.GetInt64(0),
                new StationJobId(Guid.Parse(reader.GetString(1))),
                DateTimeOffset.ParseExact(
                    reader.GetString(2),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None))
            : null;
    }

    private static async ValueTask UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationJobId jobId,
        StationResourceFenceEvidence fence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_resource_fences (
                resource_kind,
                resource_id,
                fencing_token,
                owner_job_id,
                expires_at_utc)
            VALUES (
                $resource_kind,
                $resource_id,
                $fencing_token,
                $owner_job_id,
                $expires_at_utc)
            ON CONFLICT(resource_kind, resource_id) DO UPDATE SET
                fencing_token = excluded.fencing_token,
                owner_job_id = excluded.owner_job_id,
                expires_at_utc = excluded.expires_at_utc;
            """;
        command.Parameters.AddWithValue("$resource_kind", fence.ResourceKind);
        command.Parameters.AddWithValue("$resource_id", fence.ResourceId);
        command.Parameters.AddWithValue("$fencing_token", fence.FencingToken);
        command.Parameters.AddWithValue("$owner_job_id", jobId.Value.ToString("D"));
        command.Parameters.AddWithValue(
            "$expires_at_utc",
            fence.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                "Station Agent resource fencing requires a file-backed SQLite data source.",
                nameof(connectionString));
        }

        var path = Path.GetFullPath(builder.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        builder.DataSource = path;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        builder.Cache = SqliteCacheMode.Shared;
        return builder.ToString();
    }

    private sealed record CurrentFence(
        long FencingToken,
        StationJobId JobId,
        DateTimeOffset ExpiresAtUtc);
}
