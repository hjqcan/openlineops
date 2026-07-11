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

    public async ValueTask<IReadOnlyCollection<ResourceLease>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT resource_kind, resource_id, run_id, operation_run_id,
                   fencing_token, acquired_at_utc, expires_at_utc
            FROM runtime_resource_leases
            ORDER BY resource_kind, resource_id;
            """;
        var leases = new List<ResourceLease>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            leases.Add(new ResourceLease(
                new ResourceRequirement(
                    ParseResourceKind(reader.GetString(0)),
                    reader.GetString(1)),
                ParseProductionRunId(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4),
                ParseTimestamp(reader.GetString(5)),
                ParseTimestamp(reader.GetString(6))));
        }

        return leases;
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
            await using var transaction = connection.BeginTransaction(deferred: false);

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

            var activeOwned = await ListActiveOwnerLeasesAsync(
                    connection,
                    transaction,
                    runId,
                    operationRunId,
                    acquiredAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (activeOwned.Count > 0)
            {
                var exactResources = activeOwned
                    .Select(static lease => lease.Resource)
                    .ToHashSet()
                    .SetEquals(requested);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return exactResources ? activeOwned : null;
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

    private static async ValueTask<IReadOnlyCollection<ResourceLease>> ListActiveOwnerLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRunId runId,
        string operationRunId,
        DateTimeOffset acquiredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT resource_kind, resource_id, fencing_token, acquired_at_utc, expires_at_utc
            FROM runtime_resource_leases
            WHERE run_id = $run_id
              AND operation_run_id = $operation_run_id
              AND expires_at_utc > $acquired_at_utc
            ORDER BY resource_kind, resource_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value.ToString("D"));
        command.Parameters.AddWithValue("$operation_run_id", operationRunId);
        command.Parameters.AddWithValue(
            "$acquired_at_utc",
            acquiredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        var leases = new List<ResourceLease>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            leases.Add(new ResourceLease(
                new ResourceRequirement(
                    ParseResourceKind(reader.GetString(0)),
                    reader.GetString(1)),
                runId,
                operationRunId,
                reader.GetInt64(2),
                ParseTimestamp(reader.GetString(3)),
                ParseTimestamp(reader.GetString(4))));
        }

        return leases;
    }

    public async ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRunId);
        ArgumentNullException.ThrowIfNull(evidence);
        if (validatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Resource lease validation time must be UTC.", nameof(validatedAtUtc));
        }

        var supplied = evidence
            .OrderBy(static item => item.Resource.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        if (supplied.Length == 0
            || supplied.Any(static item => item is null)
            || supplied.Select(static item => item.Resource).Distinct().Count() != supplied.Length)
        {
            throw new ArgumentException(
                "Resource lease validation requires non-empty unique evidence.",
                nameof(evidence));
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
            foreach (var item in supplied)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT f.fencing_token,
                           l.run_id,
                           l.operation_run_id,
                           l.fencing_token,
                           l.expires_at_utc
                    FROM runtime_resource_fencing_tokens AS f
                    LEFT JOIN runtime_resource_leases AS l
                      ON l.resource_kind = f.resource_kind
                     AND l.resource_id = f.resource_id
                    WHERE f.resource_kind = $resource_kind
                      AND f.resource_id = $resource_id
                    LIMIT 1;
                    """;
                AddResourceParameters(command, item.Resource);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                var valid = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    && reader.GetInt64(0) == item.FencingToken
                    && !reader.IsDBNull(1)
                    && string.Equals(
                        reader.GetString(1),
                        runId.Value.ToString("D"),
                        StringComparison.Ordinal)
                    && string.Equals(reader.GetString(2), operationRunId, StringComparison.Ordinal)
                    && reader.GetInt64(3) == item.FencingToken
                    && ParseTimestamp(reader.GetString(4)) == item.ExpiresAtUtc
                    && item.ExpiresAtUtc > validatedAtUtc;
                if (!valid)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return ResourceLeaseFenceValidationResult.Reject(
                        $"Resource lease fence {item.Resource.CanonicalKey}/{item.FencingToken} is missing, stale, expired, or owned by another Operation Run.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ResourceLeaseFenceValidationResult.Accept();
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

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    private static ResourceKind ParseResourceKind(string value) =>
        Enum.TryParse<ResourceKind>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException(
                $"Persisted resource kind '{value}' is not a canonical token.");

    private static ProductionRunId ParseProductionRunId(string value)
    {
        if (Guid.TryParseExact(value, "D", out var parsed)
            && parsed != Guid.Empty
            && string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            return new ProductionRunId(parsed);
        }

        throw new InvalidDataException(
            $"Persisted Production Run id '{value}' is not canonical.");
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
