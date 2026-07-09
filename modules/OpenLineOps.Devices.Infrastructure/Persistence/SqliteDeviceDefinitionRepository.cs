using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Infrastructure.Persistence;

public sealed class SqliteDeviceDefinitionRepository : IDeviceDefinitionRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteDeviceDefinitionRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public async ValueTask SaveAsync(
        DeviceDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = DevicePersistenceMapper.ToSnapshot(definition);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_definitions (
                definition_id,
                document_json,
                plugin_id,
                display_name,
                updated_at_utc)
            VALUES (
                $definition_id,
                $document_json,
                $plugin_id,
                $display_name,
                $updated_at_utc)
            ON CONFLICT(definition_id) DO UPDATE SET
                document_json = excluded.document_json,
                plugin_id = excluded.plugin_id,
                display_name = excluded.display_name,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$definition_id", definition.Id.Value);
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$plugin_id", definition.PluginId);
        command.Parameters.AddWithValue("$display_name", definition.DisplayName);
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DeviceDefinition?> GetByIdAsync(
        DeviceDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionId);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM device_definitions
            WHERE definition_id = $definition_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$definition_id", definitionId.Value);

        var documentJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return documentJson is null
            ? null
            : DeserializeDefinition((string)documentJson);
    }

    public async ValueTask<IReadOnlyCollection<DeviceDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM device_definitions
            ORDER BY definition_id;
            """;

        var definitions = new List<DeviceDefinition>();

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

            SqliteDeviceStorage.EnsureDatabaseDirectory(_connectionString);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS device_definitions (
                    definition_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    plugin_id TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_device_definitions_plugin
                    ON device_definitions(plugin_id);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static DeviceDefinition DeserializeDefinition(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedDeviceDefinition>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted device definition document is empty.");

        return DevicePersistenceMapper.ToAggregate(snapshot);
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
