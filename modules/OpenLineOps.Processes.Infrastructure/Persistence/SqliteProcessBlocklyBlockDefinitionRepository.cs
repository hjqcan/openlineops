using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Processes.Application.Persistence;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class SqliteProcessBlocklyBlockDefinitionRepository :
    IProcessBlocklyBlockDefinitionRepository,
    IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteProcessBlocklyBlockDefinitionRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public async ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                latest.block_type,
                latest.category,
                latest.display_name,
                latest.blockly_json,
                latest.python_code_template,
                latest.version,
                latest.created_at_utc,
                latest.updated_at_utc
            FROM process_blockly_block_definitions latest
            INNER JOIN (
                SELECT block_type, MAX(version) AS version
                FROM process_blockly_block_definitions
                GROUP BY block_type
            ) selected
                ON selected.block_type = latest.block_type
                AND selected.version = latest.version
            ORDER BY latest.block_type;
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

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                block_type,
                category,
                display_name,
                blockly_json,
                python_code_template,
                version,
                created_at_utc,
                updated_at_utc
            FROM process_blockly_block_definitions
            WHERE block_type = $block_type
            ORDER BY version DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$block_type", blockType);

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

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                block_type,
                category,
                display_name,
                blockly_json,
                python_code_template,
                version,
                created_at_utc,
                updated_at_utc
            FROM process_blockly_block_definitions
            WHERE block_type = $block_type
            ORDER BY version DESC;
            """;
        command.Parameters.AddWithValue("$block_type", blockType);

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

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

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
                $block_type,
                $version,
                $category,
                $display_name,
                $blockly_json,
                $python_code_template,
                $created_at_utc,
                $updated_at_utc);
            """;
        command.Parameters.AddWithValue("$block_type", record.BlockType);
        command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$category", record.Category);
        command.Parameters.AddWithValue("$display_name", record.DisplayName);
        command.Parameters.AddWithValue("$blockly_json", record.BlocklyJson);
        command.Parameters.AddWithValue("$python_code_template", record.PythonCodeTemplate);
        command.Parameters.AddWithValue("$created_at_utc", ToRoundTripString(record.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated_at_utc", ToRoundTripString(record.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();

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

            EnsureDatabaseDirectory();

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS process_blockly_block_definitions (
                    block_type TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    category TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    blockly_json TEXT NOT NULL,
                    python_code_template TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
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
        SqliteConnection connection,
        SqliteTransaction transaction,
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
                blockly_json,
                python_code_template,
                version,
                created_at_utc,
                updated_at_utc
            FROM process_blockly_block_definitions
            WHERE block_type = $block_type
            ORDER BY version DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$block_type", blockType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader)
            : null;
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

    private static ProcessBlocklyBlockDefinitionRecord ReadRecord(SqliteDataReader reader)
    {
        return new ProcessBlocklyBlockDefinitionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            ParseDateTimeOffset(reader.GetString(6)),
            ParseDateTimeOffset(reader.GetString(7)));
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string ToRoundTripString(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
    }
}
