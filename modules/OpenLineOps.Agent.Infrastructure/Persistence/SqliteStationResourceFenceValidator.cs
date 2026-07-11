using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class SqliteStationResourceFenceValidator :
    IStationResourceFenceValidator,
    IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _validationLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationResourceFenceValidator(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<StationResourceFenceValidationResult> ValidateAndAdvanceAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
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
            SELECT fencing_token, owner_job_id
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
                new StationJobId(Guid.Parse(reader.GetString(1))))
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

    private sealed record CurrentFence(long FencingToken, StationJobId JobId);
}
