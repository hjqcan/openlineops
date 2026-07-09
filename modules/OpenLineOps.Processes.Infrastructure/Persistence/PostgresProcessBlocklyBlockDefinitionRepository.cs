using Npgsql;
using OpenLineOps.Processes.Application.Persistence;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class PostgresProcessBlocklyBlockDefinitionRepository :
    IProcessBlocklyBlockDefinitionRepository,
    IDisposable,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public PostgresProcessBlocklyBlockDefinitionRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString.Trim());
    }

    public async ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT ON (block_type)
                block_type,
                category,
                display_name,
                blockly_json::text,
                python_code_template,
                version,
                created_at_utc,
                updated_at_utc
            FROM process_blockly_block_definitions
            ORDER BY block_type, version DESC;
            """;

        var records = new List<ProcessBlocklyBlockDefinitionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async ValueTask<ProcessBlocklyBlockDefinitionRecord?> GetLatestAsync(
        string blockType,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                block_type,
                category,
                display_name,
                blockly_json::text,
                python_code_template,
                version,
                created_at_utc,
                updated_at_utc
            FROM process_blockly_block_definitions
            WHERE block_type = @block_type
            ORDER BY version DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("block_type", blockType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    public async ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsAsync(
        string blockType,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                block_type,
                category,
                display_name,
                blockly_json::text,
                python_code_template,
                version,
                created_at_utc,
                updated_at_utc
            FROM process_blockly_block_definitions
            WHERE block_type = @block_type
            ORDER BY version DESC;
            """;
        command.Parameters.AddWithValue("block_type", blockType);

        var records = new List<ProcessBlocklyBlockDefinitionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
        string blockType,
        string category,
        string displayName,
        string blocklyJson,
        string pythonCodeTemplate,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var latest = await ReadLatestVersionAsync(
            connection,
            transaction,
            blockType,
            cancellationToken).ConfigureAwait(false);
        var record = new ProcessBlocklyBlockDefinitionRecord(
            blockType,
            category,
            displayName,
            blocklyJson,
            pythonCodeTemplate,
            latest?.Version + 1 ?? 1,
            latest?.CreatedAtUtc ?? recordedAtUtc,
            recordedAtUtc);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO process_blockly_block_definitions (
                block_type,
                version,
                category,
                display_name,
                blockly_json,
                python_code_template,
                created_at_utc,
                updated_at_utc)
            VALUES (
                @block_type,
                @version,
                @category,
                @display_name,
                @blockly_json::jsonb,
                @python_code_template,
                @created_at_utc,
                @updated_at_utc);
            """;
        command.Parameters.AddWithValue("block_type", record.BlockType);
        command.Parameters.AddWithValue("version", record.Version);
        command.Parameters.AddWithValue("category", record.Category);
        command.Parameters.AddWithValue("display_name", record.DisplayName);
        command.Parameters.AddWithValue("blockly_json", record.BlocklyJson);
        command.Parameters.AddWithValue("python_code_template", record.PythonCodeTemplate);
        command.Parameters.AddWithValue("created_at_utc", record.CreatedAtUtc);
        command.Parameters.AddWithValue("updated_at_utc", record.UpdatedAtUtc);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return record;
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
                CREATE TABLE IF NOT EXISTS process_blockly_block_definitions (
                    block_type text NOT NULL,
                    version integer NOT NULL,
                    category text NOT NULL,
                    display_name text NOT NULL,
                    blockly_json jsonb NOT NULL,
                    python_code_template text NOT NULL,
                    created_at_utc timestamptz NOT NULL,
                    updated_at_utc timestamptz NOT NULL,
                    PRIMARY KEY (block_type, version)
                );

                CREATE INDEX IF NOT EXISTS ix_process_blockly_block_definitions_block_type_version
                    ON process_blockly_block_definitions(block_type, version DESC);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async ValueTask<ProcessBlocklyBlockDefinitionRecord?> ReadLatestVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string blockType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                block_type,
                category,
                display_name,
                blockly_json::text,
                python_code_template,
                version,
                created_at_utc,
                updated_at_utc
            FROM process_blockly_block_definitions
            WHERE block_type = @block_type
            ORDER BY version DESC
            LIMIT 1
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("block_type", blockType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
    }

    private static ProcessBlocklyBlockDefinitionRecord ReadRecord(NpgsqlDataReader reader)
    {
        return new ProcessBlocklyBlockDefinitionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
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
