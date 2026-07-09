using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class SqliteProcessDefinitionRepository : IProcessDefinitionRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteProcessDefinitionRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public async ValueTask SaveAsync(
        ProcessDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await EnsureSchemaAsync(cancellationToken);

        var snapshot = ProcessDefinitionSnapshotMapper.ToSnapshot(definition);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO process_definitions (
                definition_id,
                document_json,
                status,
                updated_at_utc)
            VALUES (
                $definition_id,
                $document_json,
                $status,
                $updated_at_utc)
            ON CONFLICT(definition_id) DO UPDATE SET
                document_json = excluded.document_json,
                status = excluded.status,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$definition_id", definition.Id.Value);
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$status", definition.Status.ToString());
        command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<ProcessDefinition?> GetByIdAsync(
        ProcessDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM process_definitions
            WHERE definition_id = $definition_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$definition_id", definitionId.Value);

        var documentJson = await command.ExecuteScalarAsync(cancellationToken);
        return documentJson is null
            ? null
            : DeserializeDefinition((string)documentJson);
    }

    public async ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM process_definitions
            ORDER BY definition_id;
            """;

        var definitions = new List<ProcessDefinition>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add(DeserializeDefinition(reader.GetString(0)));
        }

        return definitions;
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
                CREATE TABLE IF NOT EXISTS process_definitions (
                    definition_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_process_definitions_status
                    ON process_definitions(status);
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

    private static ProcessDefinition DeserializeDefinition(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedProcessDefinition>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted process definition document is empty.");

        return ProcessDefinitionSnapshotMapper.ToAggregate(snapshot);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
