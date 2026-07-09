using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteRuntimeSessionRepository : IRuntimeSessionRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteRuntimeSessionRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public async ValueTask SaveAsync(
        RuntimeSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await EnsureSchemaAsync(cancellationToken);

        var snapshot = RuntimeSessionSnapshotMapper.ToSnapshot(session);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runtime_sessions (
                session_id,
                document_json,
                status,
                station_id,
                process_definition_id,
                process_version_id,
                configuration_snapshot_id,
                recipe_snapshot_id,
                last_transition_at_utc,
                updated_at_utc)
            VALUES (
                $session_id,
                $document_json,
                $status,
                $station_id,
                $process_definition_id,
                $process_version_id,
                $configuration_snapshot_id,
                $recipe_snapshot_id,
                $last_transition_at_utc,
                $updated_at_utc)
            ON CONFLICT(session_id) DO UPDATE SET
                document_json = excluded.document_json,
                status = excluded.status,
                station_id = excluded.station_id,
                process_definition_id = excluded.process_definition_id,
                process_version_id = excluded.process_version_id,
                configuration_snapshot_id = excluded.configuration_snapshot_id,
                recipe_snapshot_id = excluded.recipe_snapshot_id,
                last_transition_at_utc = excluded.last_transition_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$session_id", session.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$station_id", session.StationId.Value);
        command.Parameters.AddWithValue("$process_definition_id", session.ProcessDefinitionId.Value);
        command.Parameters.AddWithValue("$process_version_id", session.ProcessVersionId.Value);
        command.Parameters.AddWithValue("$configuration_snapshot_id", session.ConfigurationSnapshotId.Value);
        command.Parameters.AddWithValue("$recipe_snapshot_id", session.RecipeSnapshotId.Value);
        command.Parameters.AddWithValue("$last_transition_at_utc", FormatTimestamp(session.LastTransitionAtUtc));
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<RuntimeSession?> GetByIdAsync(
        RuntimeSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM runtime_sessions
            WHERE session_id = $session_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Value.ToString("D"));

        var documentJson = await command.ExecuteScalarAsync(cancellationToken);
        return documentJson is null
            ? null
            : DeserializeSession((string)documentJson);
    }

    public async ValueTask<IReadOnlyCollection<RuntimeSession>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM runtime_sessions
            WHERE status NOT IN ('Stopped', 'Completed', 'Failed', 'Canceled')
            ORDER BY last_transition_at_utc, session_id;
            """;

        var sessions = new List<RuntimeSession>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(DeserializeSession(reader.GetString(0)));
        }

        return sessions;
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            EnsureDatabaseDirectory();

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS runtime_sessions (
                    session_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    process_definition_id TEXT NOT NULL,
                    process_version_id TEXT NOT NULL,
                    configuration_snapshot_id TEXT NOT NULL,
                    recipe_snapshot_id TEXT NOT NULL,
                    last_transition_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_runtime_sessions_recovery
                    ON runtime_sessions(status, last_transition_at_utc);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
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

    private static RuntimeSession DeserializeSession(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedRuntimeSession>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted runtime session document is empty.");

        return RuntimeSessionSnapshotMapper.ToAggregate(snapshot);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
