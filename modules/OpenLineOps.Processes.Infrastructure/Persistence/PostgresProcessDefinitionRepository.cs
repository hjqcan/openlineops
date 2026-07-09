using System.Text.Json;
using Npgsql;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class PostgresProcessDefinitionRepository :
    IProcessDefinitionRepository,
    IDisposable,
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public PostgresProcessDefinitionRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString.Trim());
    }

    public async ValueTask SaveAsync(
        ProcessDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = ProcessDefinitionSnapshotMapper.ToSnapshot(definition);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO process_definitions (
                definition_id,
                document_json,
                status,
                updated_at_utc)
            VALUES (
                @definition_id,
                @document_json::jsonb,
                @status,
                @updated_at_utc)
            ON CONFLICT (definition_id) DO UPDATE SET
                document_json = EXCLUDED.document_json,
                status = EXCLUDED.status,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """;
        command.Parameters.AddWithValue("definition_id", definition.Id.Value);
        command.Parameters.AddWithValue("document_json", documentJson);
        command.Parameters.AddWithValue("status", definition.Status.ToString());
        command.Parameters.AddWithValue("updated_at_utc", DateTimeOffset.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProcessDefinition?> GetByIdAsync(
        ProcessDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text
            FROM process_definitions
            WHERE definition_id = @definition_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("definition_id", definitionId.Value);

        var documentJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return documentJson is null
            ? null
            : DeserializeDefinition((string)documentJson);
    }

    public async ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json::text
            FROM process_definitions
            ORDER BY definition_id;
            """;

        var definitions = new List<ProcessDefinition>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
                CREATE TABLE IF NOT EXISTS process_definitions (
                    definition_id text NOT NULL PRIMARY KEY,
                    document_json jsonb NOT NULL,
                    status text NOT NULL,
                    updated_at_utc timestamptz NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_process_definitions_status
                    ON process_definitions(status);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
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
        _dataSource.Dispose();
        _schemaLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        _schemaLock.Dispose();
    }
}
