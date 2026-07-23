using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeSessionRepositoryImmutabilityTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryRepositoryAllowsExactTerminalReplayAndRejectsChangedEvidence()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var session = CreateTerminalSession();
        var terminalEvents = session.DomainEvents.ToArray();

        await repository.SaveAsync(session, terminalEvents);
        var saveCount = repository.SaveCount;
        await repository.SaveAsync(session, terminalEvents);

        var changed = ReplaceTerminalIncident(session);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.SaveAsync(changed, []));

        Assert.Contains("immutable", exception.Message, StringComparison.Ordinal);
        Assert.Equal(saveCount, repository.SaveCount);
        var persisted = Assert.IsType<RuntimeSession>(await repository.GetByIdAsync(session.Id));
        Assert.Equal("Runtime.OriginalEvidence", Assert.Single(persisted.Incidents).Code);
    }

    [Fact]
    public async Task SqliteRepositoryAllowsExactTerminalReplayWithoutRewriteAndRejectsChangedEvidence()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps",
            $"runtime-terminal-immutability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connectionString =
            $"Data Source={Path.Combine(root, "runtime.sqlite")};Pooling=False";
        try
        {
            using var repository = new SqliteRuntimeSessionRepository(connectionString);
            var session = CreateTerminalSession();
            var terminalEvents = session.DomainEvents.ToArray();
            await repository.SaveAsync(session, terminalEvents);
            var firstDatabaseState = await ReadDatabaseStateAsync(connectionString, session.Id);

            await repository.SaveAsync(session, terminalEvents);
            var replayedDatabaseState = await ReadDatabaseStateAsync(connectionString, session.Id);
            Assert.Equal(firstDatabaseState, replayedDatabaseState);

            var changed = ReplaceTerminalIncident(session);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await repository.SaveAsync(changed, []));

            Assert.Contains("immutable", exception.Message, StringComparison.Ordinal);
            var persisted = Assert.IsType<RuntimeSession>(await repository.GetByIdAsync(session.Id));
            Assert.Equal("Runtime.OriginalEvidence", Assert.Single(persisted.Incidents).Code);
            Assert.Equal(firstDatabaseState, await ReadDatabaseStateAsync(connectionString, session.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RuntimeSession CreateTerminalSession()
    {
        var session = RuntimeSession.Create(
            RuntimeSessionId.New(),
            new StationId("station-terminal-evidence"),
            new ProcessDefinitionId("process-terminal-evidence"),
            new ProcessVersionId("process-terminal-evidence@release"),
            new ConfigurationSnapshotId("configuration-terminal-evidence"),
            new RecipeSnapshotId("recipe-terminal-evidence"),
            BaseTimeUtc,
            RuntimeTestReleaseIdentity.TraceMetadata());
        Assert.True(session.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        session.RecordIncident(
            RuntimeIncidentSeverity.Warning,
            "Runtime.OriginalEvidence",
            "Original terminal evidence.",
            BaseTimeUtc.AddSeconds(2));
        Assert.True(session.Complete(BaseTimeUtc.AddSeconds(3)).Succeeded);
        return session;
    }

    private static RuntimeSession ReplaceTerminalIncident(RuntimeSession source)
    {
        var replacement = RuntimeIncident.Record(
            RuntimeIncidentId.New(),
            RuntimeIncidentSeverity.Warning,
            "Runtime.ReplacedEvidence",
            "Late write with a backdated timestamp.",
            BaseTimeUtc.AddSeconds(2));
        return RuntimeSession.Restore(
            source.Id,
            source.StationId,
            source.ProcessDefinitionId,
            source.ProcessVersionId,
            source.ConfigurationSnapshotId,
            source.RecipeSnapshotId,
            source.Status,
            source.CreatedAtUtc,
            source.LastTransitionAtUtc,
            source.StartedAtUtc,
            source.PausedAtUtc,
            source.CompletedAtUtc,
            source.Steps,
            source.Commands,
            [replacement],
            source.TraceMetadata);
    }

    private static async Task<RuntimeSessionDatabaseState> ReadDatabaseStateAsync(
        string connectionString,
        RuntimeSessionId sessionId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT updated_at_utc,
                   (SELECT COUNT(*) FROM runtime_monitoring_events)
            FROM runtime_sessions
            WHERE session_id = $session_id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new RuntimeSessionDatabaseState(reader.GetString(0), reader.GetInt64(1));
    }

    private sealed record RuntimeSessionDatabaseState(
        string UpdatedAtUtc,
        long MonitoringEventCount);
}
