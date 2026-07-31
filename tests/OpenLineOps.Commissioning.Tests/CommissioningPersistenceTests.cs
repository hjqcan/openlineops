using Microsoft.Data.Sqlite;
using OpenLineOps.Commissioning.Application.Persistence;
using OpenLineOps.Commissioning.Domain.Identifiers;
using OpenLineOps.Commissioning.Domain.Sessions;
using OpenLineOps.Commissioning.Infrastructure.Persistence;

namespace OpenLineOps.Commissioning.Tests;

public sealed class CommissioningPersistenceTests
{
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 31, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryRepositoryRejectsStaleRevision()
    {
        var repository = new InMemoryCommissioningSessionRepository();
        var session = CreateSession("session-a", "station-a");
        Assert.Equal(
            CommissioningAddResult.Added,
            await repository.TryAddAsync(session));
        var first = await repository.GetByIdAsync(session.Id);
        var stale = await repository.GetByIdAsync(session.Id);
        first!.Session.RecordDiagnosticAccess(
            "engineer-a",
            "device-a",
            StartedAtUtc.AddMinutes(1));
        stale!.Session.StartSignalMonitoring(
            "engineer-a",
            "signals-a",
            StartedAtUtc.AddMinutes(1));

        await repository.SaveAsync(first.Session, first.Revision);

        await Assert.ThrowsAsync<CommissioningSessionConcurrencyException>(
            async () => await repository.SaveAsync(stale.Session, stale.Revision));
    }

    [Fact]
    public async Task SqliteColdRestartRestoresRevisionStateAndAudit()
    {
        using var database = new TemporaryDatabase();
        var session = CreateSession("session-a", "station-a");
        using (var repository =
               new SqliteCommissioningSessionRepository(database.ConnectionString))
        {
            Assert.Equal(
                CommissioningAddResult.Added,
                await repository.TryAddAsync(session));
            var loaded = await repository.GetByIdAsync(session.Id);
            loaded!.Session.RecordDiagnosticAccess(
                "engineer-a",
                "device-a",
                StartedAtUtc.AddMinutes(1));
            Assert.Equal(
                1,
                await repository.SaveAsync(loaded.Session, loaded.Revision));
        }

        using var restarted =
            new SqliteCommissioningSessionRepository(database.ConnectionString);
        var restored = await restarted.GetByIdAsync(session.Id);

        Assert.NotNull(restored);
        Assert.Equal(1, restored.Revision);
        Assert.Equal(2, restored.Session.AuditTrail.Count);
        Assert.Equal(
            CommissioningAuditKind.DiagnosticAccessed,
            restored.Session.AuditTrail[^1].Kind);
    }

    [Fact]
    public async Task SqliteEnforcesExclusiveStationLeaseAcrossRepositoryInstances()
    {
        using var database = new TemporaryDatabase();
        using var first =
            new SqliteCommissioningSessionRepository(database.ConnectionString);
        using var second =
            new SqliteCommissioningSessionRepository(database.ConnectionString);

        var results = await Task.WhenAll(
            first.TryAddAsync(CreateSession("session-a", "station-a")).AsTask(),
            second.TryAddAsync(CreateSession("session-b", "station-a")).AsTask());

        Assert.Single(results, result => result == CommissioningAddResult.Added);
        Assert.Single(
            results,
            result => result == CommissioningAddResult.StationLeaseConflict);
    }

    [Fact]
    public async Task SqliteRejectsStaleRevisionAfterColdReload()
    {
        using var database = new TemporaryDatabase();
        using var repository =
            new SqliteCommissioningSessionRepository(database.ConnectionString);
        var session = CreateSession("session-a", "station-a");
        await repository.TryAddAsync(session);
        var first = await repository.GetByIdAsync(session.Id);
        var stale = await repository.GetByIdAsync(session.Id);
        first!.Session.RecordDiagnosticAccess(
            "engineer-a",
            "device-a",
            StartedAtUtc.AddMinutes(1));
        stale!.Session.StartSignalMonitoring(
            "engineer-a",
            "signals-a",
            StartedAtUtc.AddMinutes(1));
        await repository.SaveAsync(first.Session, first.Revision);

        await Assert.ThrowsAsync<CommissioningSessionConcurrencyException>(
            async () => await repository.SaveAsync(stale.Session, stale.Revision));
    }

    [Fact]
    public async Task SqliteRejectsAnyAttemptToRewritePersistedAuditPrefix()
    {
        using var database = new TemporaryDatabase();
        using var repository =
            new SqliteCommissioningSessionRepository(database.ConnectionString);
        var session = CreateSession("session-a", "station-a");
        await repository.TryAddAsync(session);
        var stored = await repository.GetByIdAsync(session.Id);
        var snapshot = stored!.Session.ToSnapshot();
        var rewrittenFirst = snapshot.AuditTrail.First() with
        {
            Reason = "rewritten history"
        };
        var candidate = CommissioningSession.Restore(
            snapshot with
            {
                AuditTrail =
                [
                    rewrittenFirst,
                    new CommissioningAuditEntry(
                        2,
                        CommissioningAuditKind.DiagnosticAccessed,
                        "engineer-a",
                        StartedAtUtc.AddMinutes(1),
                        "device-a",
                        "Read-only device diagnostics accessed.",
                        snapshot.FencingToken)
                ]
            });

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.SaveAsync(candidate, stored.Revision));
    }

    [Fact]
    public async Task SqliteDatabaseTriggerRejectsDirectAuditMutation()
    {
        using var database = new TemporaryDatabase();
        using var repository =
            new SqliteCommissioningSessionRepository(database.ConnectionString);
        await repository.TryAddAsync(CreateSession("session-a", "station-a"));
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE commissioning_audit
            SET reason = 'rewritten'
            WHERE session_id = 'session-a' AND sequence = 1;
            """;

        var exception = await Assert.ThrowsAsync<SqliteException>(
            async () => await command.ExecuteNonQueryAsync());

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void SqliteRequiresFileBackedStorage()
    {
        Assert.Throws<ArgumentException>(() =>
            new SqliteCommissioningSessionRepository("Data Source=:memory:"));
    }

    private static CommissioningSession CreateSession(
        string sessionId,
        string stationId) =>
        CommissioningSession.Start(
            new CommissioningSessionId(sessionId),
            stationId,
            "engineer-a",
            "CommissioningEngineer",
            $"lease.{sessionId}",
            41,
            StartedAtUtc,
            StartedAtUtc.AddHours(2),
            Enum.GetValues<CommissioningCapability>());

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"openlineops-commissioning-{Guid.NewGuid():N}");

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "commissioning.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
        }

        public string ConnectionString { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
