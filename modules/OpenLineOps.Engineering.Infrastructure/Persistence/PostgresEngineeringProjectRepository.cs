using System.Text.Json;
using Npgsql;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class PostgresEngineeringProjectRepository :
    IEngineeringProjectRepository,
    IDisposable,
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public PostgresEngineeringProjectRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString.Trim());
    }

    public async Task SaveAsync(
        EngineeringProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = EngineeringSnapshotMapper.ToSnapshot(project);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO engineering_projects (
                project_id,
                document_json,
                workspace_id,
                active_snapshot_id,
                updated_at_utc)
            VALUES (
                @project_id,
                @document_json::jsonb,
                @workspace_id,
                @active_snapshot_id,
                @updated_at_utc)
            ON CONFLICT (project_id) DO UPDATE SET
                document_json = EXCLUDED.document_json,
                workspace_id = EXCLUDED.workspace_id,
                active_snapshot_id = EXCLUDED.active_snapshot_id,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """;
        command.Parameters.AddWithValue("project_id", project.Id.Value);
        command.Parameters.AddWithValue("document_json", documentJson);
        command.Parameters.AddWithValue("workspace_id", project.WorkspaceId.Value);
        command.Parameters.AddWithValue("active_snapshot_id", (object?)project.ActiveSnapshotId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at_utc", DateTimeOffset.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EngineeringProject?> GetByIdAsync(
        EngineeringProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text
            FROM engineering_projects
            WHERE project_id = @project_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("project_id", projectId.Value);

        var documentJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return documentJson is null
            ? null
            : DeserializeProject((string)documentJson);
    }

    public async Task<IReadOnlyCollection<EngineeringProject>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text
            FROM engineering_projects
            ORDER BY project_id;
            """;

        var projects = new List<EngineeringProject>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(DeserializeProject(reader.GetString(0)));
        }

        return projects;
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

            await using var connection = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS engineering_projects (
                    project_id text NOT NULL PRIMARY KEY,
                    document_json jsonb NOT NULL,
                    workspace_id text NOT NULL,
                    active_snapshot_id text NULL,
                    updated_at_utc timestamptz NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_engineering_projects_workspace
                    ON engineering_projects(workspace_id);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static EngineeringProject DeserializeProject(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedEngineeringProject>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted engineering project document is empty.");

        return EngineeringSnapshotMapper.ToAggregate(snapshot);
    }

    public void Dispose()
    {
        _dataSource.Dispose();
        _schemaLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        _schemaLock.Dispose();
    }
}
