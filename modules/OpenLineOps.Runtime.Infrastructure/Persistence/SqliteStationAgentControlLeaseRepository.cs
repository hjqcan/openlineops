using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteStationAgentControlLeaseRepository :
    IStationAgentControlLeaseRepository,
    IDisposable
{
    private readonly string _connectionString;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _schemaCreated;

    public SqliteStationAgentControlLeaseRepository(
        string connectionString,
        IClock clock)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<StationAgentControlLeaseObservation> GetAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var observedAtUtc = ReadStoreUtcNow();
        var lease = await ReadLeaseAsync(
                connection,
                transaction: null,
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureClockDidNotMoveBackward(lease, observedAtUtc);
        return new StationAgentControlLeaseObservation(lease, observedAtUtc);
    }

    public async ValueTask<StationAgentControlLeaseMutationResult> TryAcquireAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        string leaseProofSha256,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(stationId, ownerAgentId, ownerInstanceId);
        _ = StationAgentControlLease.RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        ValidateDuration(duration);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var acquiredAtUtc = ReadStoreUtcNow();
            var current = await ReadLeaseAsync(
                    connection,
                    transaction,
                    stationId,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureClockDidNotMoveBackward(current, acquiredAtUtc);
            if (current?.IsActiveAt(acquiredAtUtc) == true)
            {
                var result = new StationAgentControlLeaseMutationResult(
                    current.IsOwnedBy(ownerAgentId, ownerInstanceId)
                        ? current.MatchesProofSha256(leaseProofSha256)
                            ? StationAgentControlLeaseMutationStatus.AlreadyOwned
                            : StationAgentControlLeaseMutationStatus.StaleProof
                        : StationAgentControlLeaseMutationStatus.HeldByAnotherOwner,
                    current,
                    acquiredAtUtc);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }

            var token = checked((current?.FencingToken ?? 0) + 1);
            var acquired = new StationAgentControlLease(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                token,
                leaseProofSha256,
                acquiredAtUtc,
                acquiredAtUtc,
                acquiredAtUtc.Add(duration));
            await UpsertAsync(connection, transaction, acquired, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.Acquired,
                acquired,
                acquiredAtUtc);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask<StationAgentControlLeaseMutationResult> TryRenewAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        _ = StationAgentControlLease.RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        ValidateDuration(duration);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var renewedAtUtc = ReadStoreUtcNow();
            var current = await ReadLeaseAsync(
                    connection,
                    transaction,
                    stationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Missing(renewedAtUtc);
            }

            EnsureClockDidNotMoveBackward(current, renewedAtUtc);
            var rejected = RejectStaleClaim(
                current,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                leaseProofSha256,
                renewedAtUtc);
            if (rejected is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return rejected;
            }

            var renewed = new StationAgentControlLease(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                current.LeaseProofSha256,
                current.AcquiredAtUtc,
                renewedAtUtc,
                renewedAtUtc.Add(duration));
            await UpsertAsync(connection, transaction, renewed, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.Renewed,
                renewed,
                renewedAtUtc);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask<StationAgentControlLeaseValidationResult>
        ValidateGenerationAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var observedAtUtc = ReadStoreUtcNow();
        var current = await ReadLeaseAsync(
                connection,
                transaction,
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureClockDidNotMoveBackward(current, observedAtUtc);
        var observation = new StationAgentControlLeaseObservation(
            current,
            observedAtUtc);
        var valid = observation.IsActive
            && current!.IsOwnedBy(ownerAgentId, ownerInstanceId)
            && current.FencingToken == fencingToken;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StationAgentControlLeaseValidationResult(valid, observation);
    }

    public async ValueTask<StationAgentControlLeaseValidationResult>
        ValidateProofAsync(
            StationId stationId,
            string ownerAgentId,
            string ownerInstanceId,
            long fencingToken,
            string leaseProofSha256,
            CancellationToken cancellationToken = default)
    {
        _ = StationAgentControlLease.RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        var generation = await ValidateGenerationAsync(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                cancellationToken)
            .ConfigureAwait(false);
        return new StationAgentControlLeaseValidationResult(
            generation.IsValid
                && generation.Observation.Lease!.MatchesProofSha256(
                    leaseProofSha256),
            generation.Observation);
    }

    public async ValueTask<StationAgentControlLeaseMutationResult> TryReleaseAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        _ = StationAgentControlLease.RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var releasedAtUtc = ReadStoreUtcNow();
            var current = await ReadLeaseAsync(
                    connection,
                    transaction,
                    stationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Missing(releasedAtUtc);
            }

            EnsureClockDidNotMoveBackward(current, releasedAtUtc);
            var rejected = RejectStaleClaim(
                current,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                leaseProofSha256,
                releasedAtUtc);
            if (rejected is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return rejected;
            }

            var released = new StationAgentControlLease(
                stationId,
                current.OwnerAgentId,
                current.OwnerInstanceId,
                current.FencingToken,
                current.LeaseProofSha256,
                current.AcquiredAtUtc,
                releasedAtUtc,
                releasedAtUtc);
            await UpsertAsync(connection, transaction, released, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.Released,
                released,
                releasedAtUtc);
        }
        finally
        {
            _writeLock.Release();
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
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            EnsureDatabaseDirectory();
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS station_agent_control_leases (
                    station_id TEXT NOT NULL PRIMARY KEY,
                    owner_agent_id TEXT NOT NULL,
                    owner_instance_id TEXT NOT NULL,
                    fencing_token INTEGER NOT NULL CHECK(fencing_token >= 1),
                    lease_proof_sha256 TEXT NOT NULL,
                    acquired_at_utc TEXT NOT NULL,
                    renewed_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_station_agent_control_leases_expiry
                    ON station_agent_control_leases(expires_at_utc, station_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureLeaseProofColumnAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask EnsureLeaseProofColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(station_agent_control_leases);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var found = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(
                    reader.GetString(1),
                    "lease_proof_sha256",
                    StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        if (found)
        {
            return;
        }

        await using var migrate = connection.CreateCommand();
        migrate.CommandText = """
            ALTER TABLE station_agent_control_leases
            ADD COLUMN lease_proof_sha256 TEXT NOT NULL
            DEFAULT '0000000000000000000000000000000000000000000000000000000000000000';
            """;
        _ = await migrate.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<StationAgentControlLease?> ReadLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        StationId stationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT station_id,
                   owner_agent_id,
                   owner_instance_id,
                   fencing_token,
                   lease_proof_sha256,
                   acquired_at_utc,
                   renewed_at_utc,
                   expires_at_utc
            FROM station_agent_control_leases
            WHERE station_id = $station_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$station_id", stationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var persistedStationId = new StationId(reader.GetString(0));
        if (persistedStationId != stationId)
        {
            throw new InvalidDataException(
                $"Persisted station Agent control lease identity does not match "
                + $"key {stationId}.");
        }

        return new StationAgentControlLease(
            persistedStationId,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)));
    }

    private static async ValueTask UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StationAgentControlLease lease,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO station_agent_control_leases (
                station_id,
                owner_agent_id,
                owner_instance_id,
                fencing_token,
                lease_proof_sha256,
                acquired_at_utc,
                renewed_at_utc,
                expires_at_utc)
            VALUES (
                $station_id,
                $owner_agent_id,
                $owner_instance_id,
                $fencing_token,
                $lease_proof_sha256,
                $acquired_at_utc,
                $renewed_at_utc,
                $expires_at_utc)
            ON CONFLICT(station_id)
            DO UPDATE SET
                owner_agent_id = excluded.owner_agent_id,
                owner_instance_id = excluded.owner_instance_id,
                fencing_token = excluded.fencing_token,
                lease_proof_sha256 = excluded.lease_proof_sha256,
                acquired_at_utc = excluded.acquired_at_utc,
                renewed_at_utc = excluded.renewed_at_utc,
                expires_at_utc = excluded.expires_at_utc;
            """;
        command.Parameters.AddWithValue("$station_id", lease.StationId.Value);
        command.Parameters.AddWithValue("$owner_agent_id", lease.OwnerAgentId);
        command.Parameters.AddWithValue("$owner_instance_id", lease.OwnerInstanceId);
        command.Parameters.AddWithValue("$fencing_token", lease.FencingToken);
        command.Parameters.AddWithValue(
            "$lease_proof_sha256",
            lease.LeaseProofSha256);
        command.Parameters.AddWithValue(
            "$acquired_at_utc",
            FormatTimestamp(lease.AcquiredAtUtc));
        command.Parameters.AddWithValue(
            "$renewed_at_utc",
            FormatTimestamp(lease.RenewedAtUtc));
        command.Parameters.AddWithValue(
            "$expires_at_utc",
            FormatTimestamp(lease.ExpiresAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static StationAgentControlLeaseMutationResult? RejectStaleClaim(
        StationAgentControlLease current,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        DateTimeOffset observedAtUtc)
    {
        if (!current.IsActiveAt(observedAtUtc))
        {
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.Expired,
                current,
                observedAtUtc);
        }

        if (!current.IsOwnedBy(ownerAgentId, ownerInstanceId))
        {
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.StaleOwner,
                current,
                observedAtUtc);
        }

        if (!current.MatchesProofSha256(leaseProofSha256))
        {
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.StaleProof,
                current,
                observedAtUtc);
        }

        return current.FencingToken == fencingToken
            ? null
            : new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.StaleFencingToken,
                current,
                observedAtUtc);
    }

    private static StationAgentControlLeaseMutationResult Missing(
        DateTimeOffset observedAtUtc) => new(
            StationAgentControlLeaseMutationStatus.NotFound,
            null,
            observedAtUtc);

    private static void ValidateRequest(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        _ = StationAgentControlLease.RequireOwnerIdentity(
            ownerAgentId,
            nameof(ownerAgentId));
        _ = StationAgentControlLease.RequireOwnerInstanceId(
            ownerInstanceId,
            nameof(ownerInstanceId));
    }

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Station Agent control lease duration must be positive and no "
                + "longer than five minutes.");
        }
    }

    private DateTimeOffset ReadStoreUtcNow()
    {
        var utcNow = _clock.UtcNow;
        return utcNow == default || utcNow.Offset != TimeSpan.Zero
            ? throw new InvalidOperationException(
                "Station Agent control lease store clock must return non-default UTC.")
            : utcNow;
    }

    private static void EnsureClockDidNotMoveBackward(
        StationAgentControlLease? current,
        DateTimeOffset observedAtUtc)
    {
        if (current is not null && observedAtUtc < current.RenewedAtUtc)
        {
            throw new InvalidOperationException(
                "Station Agent control lease store clock moved backward.");
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static string RequireFileBackedConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "SQLite connection string is required.",
                nameof(connectionString));
        }

        var normalized = connectionString.Trim();
        var builder = new SqliteConnectionStringBuilder(normalized);
        if (builder.Mode == SqliteOpenMode.Memory
            || builder.DataSource.Contains(
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
            || (builder.DataSource.StartsWith(
                    "file:",
                    StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains(
                    "mode=memory",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Station Agent control lease SQLite persistence requires a "
                + "file-backed database; use the InMemory provider for transient execution.",
                nameof(connectionString));
        }

        return normalized;
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    public void Dispose()
    {
        _schemaLock.Dispose();
        _writeLock.Dispose();
    }
}
