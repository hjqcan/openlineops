using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Commissioning.Application.Persistence;
using OpenLineOps.Commissioning.Domain.Identifiers;
using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Infrastructure.Persistence;

public sealed class SqliteCommissioningSessionRepository :
    ICommissioningSessionRepository,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteCommissioningSessionRepository(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<CommissioningAddResult> TryAddAsync(
        CommissioningSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!CommissioningPersistenceGuard.HoldsLease(session.Status))
        {
            throw new ArgumentException(
                "A new commissioning session must hold an active station lease.",
                nameof(session));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await SessionExistsAsync(
                connection,
                transaction,
                session.Id.Value,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CommissioningAddResult.SessionAlreadyExists;
        }

        try
        {
            await InsertSessionAsync(
                    connection,
                    transaction,
                    session.ToSnapshot(),
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            session.ClearDomainEvents();
            return CommissioningAddResult.Added;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return CommissioningAddResult.StationLeaseConflict;
        }
    }

    public async ValueTask<CommissioningSessionPersistenceEntry?> GetByIdAsync(
        CommissioningSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(
                connection,
                transaction: null,
                "session_id = $identity",
                sessionId.Value,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<CommissioningSessionPersistenceEntry?> GetLeaseHolderAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId)
            || !string.Equals(stationId, stationId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Station ID must be non-empty canonical text.",
                nameof(stationId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(
                connection,
                transaction: null,
                "station_id = $identity AND status IN ('Active', 'RecoveryRequired')",
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<long> SaveAsync(
        CommissioningSession session,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var stored = await LoadAsync(
                connection,
                transaction,
                "session_id = $identity",
                session.Id.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (stored is null || stored.Revision != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CommissioningSessionConcurrencyException(
                session.Id,
                expectedRevision);
        }

        var storedSnapshot = stored.Session.ToSnapshot();
        var candidate = session.ToSnapshot();
        CommissioningPersistenceGuard.ValidateAppend(storedSnapshot, candidate);
        var nextRevision = checked(expectedRevision + 1);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE commissioning_sessions
                SET revision = $next_revision,
                    fencing_token = $fencing_token,
                    expires_at_utc = $expires_at_utc,
                    status = $status,
                    state_json = $state_json
                WHERE session_id = $session_id
                  AND revision = $expected_revision;
                """;
            AddCurrentStateParameters(command, candidate);
            command.Parameters.AddWithValue("$next_revision", nextRevision);
            command.Parameters.AddWithValue("$expected_revision", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CommissioningSessionConcurrencyException(
                    session.Id,
                    expectedRevision);
            }
        }

        foreach (var audit in candidate.AuditTrail.Skip(storedSnapshot.AuditTrail.Count))
        {
            await InsertAuditAsync(
                    connection,
                    transaction,
                    session.Id.Value,
                    audit,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        session.ClearDomainEvents();
        return nextRevision;
    }

    private static async ValueTask InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CommissioningSessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO commissioning_sessions (
                    session_id, station_id, lease_id, requested_by, authorized_role,
                    revision, fencing_token, started_at_utc, expires_at_utc,
                    status, state_json)
                VALUES (
                    $session_id, $station_id, $lease_id, $requested_by, $authorized_role,
                    0, $fencing_token, $started_at_utc, $expires_at_utc,
                    $status, $state_json);
                """;
            AddCurrentStateParameters(command, snapshot);
            command.Parameters.AddWithValue("$station_id", snapshot.StationId);
            command.Parameters.AddWithValue("$lease_id", snapshot.LeaseId);
            command.Parameters.AddWithValue("$requested_by", snapshot.RequestedBy);
            command.Parameters.AddWithValue("$authorized_role", snapshot.AuthorizedRole);
            command.Parameters.AddWithValue(
                "$started_at_utc",
                FormatTimestamp(snapshot.StartedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var audit in snapshot.AuditTrail)
        {
            await InsertAuditAsync(
                    connection,
                    transaction,
                    snapshot.SessionId,
                    audit,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CommissioningAuditEntry audit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO commissioning_audit (
                session_id, sequence, kind, actor_id, occurred_at_utc,
                subject_id, reason, fencing_token, document_json)
            VALUES (
                $session_id, $sequence, $kind, $actor_id, $occurred_at_utc,
                $subject_id, $reason, $fencing_token, $document_json);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$sequence", audit.Sequence);
        command.Parameters.AddWithValue("$kind", audit.Kind.ToString());
        command.Parameters.AddWithValue("$actor_id", audit.ActorId);
        command.Parameters.AddWithValue(
            "$occurred_at_utc",
            FormatTimestamp(audit.OccurredAtUtc));
        command.Parameters.AddWithValue(
            "$subject_id",
            (object?)audit.SubjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)audit.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$fencing_token", audit.FencingToken);
        command.Parameters.AddWithValue(
            "$document_json",
            JsonSerializer.Serialize(audit, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CommissioningSessionPersistenceEntry?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string predicate,
        string identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT session_id, revision, state_json
            FROM commissioning_sessions
            WHERE {predicate}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$identity", identity);
        string? sessionId = null;
        long revision = 0;
        CommissioningStateDocument? state = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            sessionId = reader.GetString(0);
            revision = reader.GetInt64(1);
            state = JsonSerializer.Deserialize<CommissioningStateDocument>(
                        reader.GetString(2),
                        JsonOptions)
                    ?? throw new InvalidDataException(
                        "Persisted commissioning state document is empty.");
        }

        if (!string.Equals(sessionId, state.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Persisted commissioning session identity does not match its state document.");
        }

        var audits = await LoadAuditAsync(
                connection,
                transaction,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
        var snapshot = state.ToSnapshot(audits);
        var restored = CommissioningSession.Restore(snapshot);
        return new CommissioningSessionPersistenceEntry(restored, revision);
    }

    private static async ValueTask<IReadOnlyCollection<CommissioningAuditEntry>> LoadAuditAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT document_json
            FROM commissioning_audit
            WHERE session_id = $session_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        var audits = new List<CommissioningAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            audits.Add(
                JsonSerializer.Deserialize<CommissioningAuditEntry>(
                    reader.GetString(0),
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "Persisted commissioning audit document is empty."));
        }

        return audits;
    }

    private static async ValueTask<bool> SessionExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM commissioning_sessions
                WHERE session_id = $session_id);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
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
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS commissioning_sessions (
                    session_id TEXT NOT NULL PRIMARY KEY,
                    station_id TEXT NOT NULL,
                    lease_id TEXT NOT NULL UNIQUE,
                    requested_by TEXT NOT NULL,
                    authorized_role TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision >= 0),
                    fencing_token INTEGER NOT NULL CHECK(fencing_token > 0),
                    started_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    status TEXT NOT NULL,
                    state_json TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_commissioning_station_lease
                    ON commissioning_sessions(station_id)
                    WHERE status IN ('Active', 'RecoveryRequired');

                CREATE INDEX IF NOT EXISTS ix_commissioning_status_expiry
                    ON commissioning_sessions(status, expires_at_utc);

                CREATE TABLE IF NOT EXISTS commissioning_audit (
                    session_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL CHECK(sequence > 0),
                    kind TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    subject_id TEXT NULL,
                    reason TEXT NULL,
                    fencing_token INTEGER NOT NULL CHECK(fencing_token > 0),
                    document_json TEXT NOT NULL,
                    PRIMARY KEY(session_id, sequence),
                    FOREIGN KEY(session_id)
                        REFERENCES commissioning_sessions(session_id)
                        ON DELETE RESTRICT
                );

                CREATE TRIGGER IF NOT EXISTS commissioning_audit_no_update
                BEFORE UPDATE ON commissioning_audit
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'commissioning audit facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS commissioning_audit_no_delete
                BEFORE DELETE ON commissioning_audit
                BEGIN
                    SELECT RAISE(
                        ABORT,
                        'commissioning audit facts are append-only');
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddCurrentStateParameters(
        SqliteCommand command,
        CommissioningSessionSnapshot snapshot)
    {
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$fencing_token", snapshot.FencingToken);
        command.Parameters.AddWithValue(
            "$expires_at_utc",
            FormatTimestamp(snapshot.ExpiresAtUtc));
        command.Parameters.AddWithValue("$status", snapshot.Status.ToString());
        command.Parameters.AddWithValue(
            "$state_json",
            JsonSerializer.Serialize(
                CommissioningStateDocument.FromSnapshot(snapshot),
                JsonOptions));
    }

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
            || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains(
                    "mode=memory",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Commissioning SQLite persistence requires a file-backed database; "
                + "use the InMemory repository for transient execution.",
                nameof(connectionString));
        }

        return normalized;
    }

    private void EnsureDatabaseDirectory()
    {
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null,
            allowIntegerValues: false));
        return options;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _schemaLock.Dispose();
    }

    private sealed record CommissioningStateDocument(
        string SessionId,
        string StationId,
        string RequestedBy,
        string AuthorizedRole,
        string LeaseId,
        long FencingToken,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        CommissioningSessionStatus Status,
        string? RecoveryReason,
        IReadOnlyCollection<CommissioningCapability> Capabilities)
    {
        public static CommissioningStateDocument FromSnapshot(
            CommissioningSessionSnapshot snapshot) =>
            new(
                snapshot.SessionId,
                snapshot.StationId,
                snapshot.RequestedBy,
                snapshot.AuthorizedRole,
                snapshot.LeaseId,
                snapshot.FencingToken,
                snapshot.StartedAtUtc,
                snapshot.ExpiresAtUtc,
                snapshot.Status,
                snapshot.RecoveryReason,
                snapshot.Capabilities);

        public CommissioningSessionSnapshot ToSnapshot(
            IReadOnlyCollection<CommissioningAuditEntry> auditTrail) =>
            new(
                SessionId,
                StationId,
                RequestedBy,
                AuthorizedRole,
                LeaseId,
                FencingToken,
                StartedAtUtc,
                ExpiresAtUtc,
                Status,
                RecoveryReason,
                Capabilities,
                auditTrail);
    }
}
