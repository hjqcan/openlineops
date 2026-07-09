using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;

namespace OpenLineOps.Devices.Infrastructure.Persistence;

public sealed class SqliteDeviceInstanceRepository : IDeviceInstanceRepository, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private int _schemaCreated;

    public SqliteDeviceInstanceRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("SQLite connection string is required.", nameof(connectionString))
            : connectionString.Trim();
    }

    public async ValueTask SaveAsync(
        DeviceInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = DevicePersistenceMapper.ToSnapshot(instance);
        var documentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_instances (
                instance_id,
                document_json,
                definition_id,
                station_id,
                status,
                updated_at_utc)
            VALUES (
                $instance_id,
                $document_json,
                $definition_id,
                $station_id,
                $status,
                $updated_at_utc)
            ON CONFLICT(instance_id) DO UPDATE SET
                document_json = excluded.document_json,
                definition_id = excluded.definition_id,
                station_id = excluded.station_id,
                status = excluded.status,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$instance_id", instance.Id.Value);
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$definition_id", instance.DefinitionId.Value);
        command.Parameters.AddWithValue("$station_id", instance.StationId);
        command.Parameters.AddWithValue("$status", instance.Status.ToString());
        command.Parameters.AddWithValue("$updated_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DeviceInstance?> GetByIdAsync(
        DeviceInstanceId instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM device_instances
            WHERE instance_id = $instance_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$instance_id", instanceId.Value);

        var documentJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return documentJson is null
            ? null
            : DeserializeInstance((string)documentJson);
    }

    public async ValueTask<IReadOnlyCollection<DeviceInstance>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM device_instances
            ORDER BY instance_id;
            """;

        return await ReadInstancesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<DeviceInstance>> ListByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return [];
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM device_instances
            WHERE station_id = $station_id
            ORDER BY instance_id;
            """;
        command.Parameters.AddWithValue("$station_id", stationId.Trim());

        return await ReadInstancesAsync(command, cancellationToken).ConfigureAwait(false);
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
                CREATE TABLE IF NOT EXISTS device_instances (
                    instance_id TEXT NOT NULL PRIMARY KEY,
                    document_json TEXT NOT NULL,
                    definition_id TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_device_instances_station
                    ON device_instances(station_id);

                CREATE INDEX IF NOT EXISTS ix_device_instances_definition_status
                    ON device_instances(definition_id, status);
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

    private static async ValueTask<IReadOnlyCollection<DeviceInstance>> ReadInstancesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var instances = new List<DeviceInstance>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            instances.Add(DeserializeInstance(reader.GetString(0)));
        }

        return instances;
    }

    private static DeviceInstance DeserializeInstance(string documentJson)
    {
        var snapshot = JsonSerializer.Deserialize<PersistedDeviceInstance>(documentJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted device instance document is empty.");

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
