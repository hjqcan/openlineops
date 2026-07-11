using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteResourceLeaseRepository : IResourceLeaseRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _schemaCreated;

    public SqliteResourceLeaseRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        DateTimeOffset acquiredAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRunId);
        ArgumentNullException.ThrowIfNull(resources);
        var requested = resources.Distinct().OrderBy(static resource => resource.CanonicalKey).ToArray();
        if (requested.Length == 0 || requested.Length != resources.Count || duration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Resource acquisition requires unique resources and a positive duration.");
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var resource in requested)
            {
                await using var conflict = connection.CreateCommand();
                conflict.Transaction = transaction;
                conflict.CommandText = """
                    SELECT run_id, operation_run_id, expires_at_utc
                    FROM runtime_resource_leases
                    WHERE resource_kind = $resource_kind
                      AND resource_id = $resource_id
                    LIMIT 1;
                    """;
                AddResourceParameters(conflict, resource);
                await using var reader = await conflict.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var ownerRunId = reader.GetString(0);
                var ownerOperationRunId = reader.GetString(1);
                var expiresAtUtc = DateTimeOffset.ParseExact(
                    reader.GetString(2),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                if (expiresAtUtc > acquiredAtUtc
                    && (!string.Equals(
                            ownerRunId,
                            runId.Value.ToString("D"),
                            StringComparison.Ordinal)
                        || !string.Equals(
                            ownerOperationRunId,
                            operationRunId,
                            StringComparison.Ordinal)))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return null;
                }
            }

            var acquired = new List<ResourceLease>(requested.Length);
            foreach (var resource in requested)
            {
                var token = await NextFencingTokenAsync(
                    connection,
                    transaction,
                    resource,
                    cancellationToken).ConfigureAwait(false);
                var lease = new ResourceLease(
                    resource,
                    runId,
                    operationRunId,
                    token,
                    acquiredAtUtc,
                    acquiredAtUtc.Add(duration));
                await UpsertLeaseAsync(connection, transaction, lease, cancellationToken)
                    .ConfigureAwait(false);
                acquired.Add(lease);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return acquired;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM runtime_resource_leases
            WHERE run_id = $run_id
              AND operation_run_id = $operation_run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_run_id", operationRunId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask HoldForRecoveryAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runtime_resource_leases
            SET expires_at_utc = $expires_at_utc
            WHERE run_id = $run_id
              AND operation_run_id = $operation_run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_run_id", operationRunId);
        command.Parameters.AddWithValue(
            "$expires_at_utc",
            DateTimeOffset.MaxValue.ToString("O", CultureInfo.InvariantCulture));
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

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS runtime_resource_fencing_tokens (
                    resource_kind TEXT NOT NULL,
                    resource_id TEXT NOT NULL,
                    fencing_token INTEGER NOT NULL,
                    PRIMARY KEY (resource_kind, resource_id)
                );

                CREATE TABLE IF NOT EXISTS runtime_resource_leases (
                    resource_kind TEXT NOT NULL,
                    resource_id TEXT NOT NULL,
                    run_id TEXT NOT NULL,
                    operation_run_id TEXT NOT NULL,
                    fencing_token INTEGER NOT NULL,
                    acquired_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    PRIMARY KEY (resource_kind, resource_id)
                );

                CREATE INDEX IF NOT EXISTS ix_runtime_resource_leases_owner
                    ON runtime_resource_leases(run_id, operation_run_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async ValueTask<long> NextFencingTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ResourceRequirement resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runtime_resource_fencing_tokens (
                resource_kind,
                resource_id,
                fencing_token)
            VALUES ($resource_kind, $resource_id, 1)
            ON CONFLICT(resource_kind, resource_id)
            DO UPDATE SET fencing_token = fencing_token + 1
            RETURNING fencing_token;
            """;
        AddResourceParameters(command, resource);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask UpsertLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ResourceLease lease,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runtime_resource_leases (
                resource_kind,
                resource_id,
                run_id,
                operation_run_id,
                fencing_token,
                acquired_at_utc,
                expires_at_utc)
            VALUES (
                $resource_kind,
                $resource_id,
                $run_id,
                $operation_run_id,
                $fencing_token,
                $acquired_at_utc,
                $expires_at_utc)
            ON CONFLICT(resource_kind, resource_id)
            DO UPDATE SET
                run_id = excluded.run_id,
                operation_run_id = excluded.operation_run_id,
                fencing_token = excluded.fencing_token,
                acquired_at_utc = excluded.acquired_at_utc,
                expires_at_utc = excluded.expires_at_utc;
            """;
        AddResourceParameters(command, lease.Resource);
        command.Parameters.AddWithValue("$run_id", lease.ProductionRunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_run_id", lease.OperationRunId);
        command.Parameters.AddWithValue("$fencing_token", lease.FencingToken);
        command.Parameters.AddWithValue("$acquired_at_utc", lease.AcquiredAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$expires_at_utc", lease.ExpiresAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddResourceParameters(
        SqliteCommand command,
        ResourceRequirement resource)
    {
        command.Parameters.AddWithValue("$resource_kind", resource.Kind.ToString());
        command.Parameters.AddWithValue("$resource_id", resource.ResourceId);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
