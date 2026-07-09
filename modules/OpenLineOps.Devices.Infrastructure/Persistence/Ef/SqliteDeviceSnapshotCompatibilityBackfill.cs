using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Instances;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

internal static class SqliteDeviceSnapshotCompatibilityBackfill
{
    private const string InitialMigrationId = "20260630101808_InitialDevicesEfSqlite";
    private const string EfCoreProductVersion = "10.0.9";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask EnsureSchemaAndBackfillAsync(
        this DevicesDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await EnsureEfSchemaAsync(context, cancellationToken).ConfigureAwait(false);

        var connection = context.Database.GetDbConnection();
        var closeWhenDone = connection.State == ConnectionState.Closed;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var definitions = await ReadSnapshotDefinitionsAsync(connection, cancellationToken).ConfigureAwait(false);
            var instances = await ReadSnapshotInstancesAsync(connection, cancellationToken).ConfigureAwait(false);
            if (definitions.Count == 0 && instances.Count == 0)
            {
                return;
            }

            var changed = await AddMissingDefinitionsAsync(context, definitions, cancellationToken)
                .ConfigureAwait(false);
            changed |= await AddMissingInstancesAsync(context, instances, cancellationToken)
                .ConfigureAwait(false);

            if (changed)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                context.ChangeTracker.Clear();
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask EnsureEfSchemaAsync(
        DevicesDbContext context,
        CancellationToken cancellationToken)
    {
        SqliteDeviceStorage.EnsureDatabaseDirectory(context.Database.GetDbConnection().ConnectionString);

        var connection = context.Database.GetDbConnection();
        var closeWhenDone = connection.State == ConnectionState.Closed;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await BootstrapExistingEfSchemaHistoryAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask BootstrapExistingEfSchemaHistoryAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await EnsureCreatedEfTablesExistAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await MigrationHistoryContainsInitialMigrationAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        using var createHistoryCommand = connection.CreateCommand();
        createHistoryCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """;
        await createHistoryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var insertHistoryCommand = connection.CreateCommand();
        insertHistoryCommand.CommandText = """
            INSERT OR IGNORE INTO "__EFMigrationsHistory" (
                "MigrationId",
                "ProductVersion")
            VALUES (
                $migration_id,
                $product_version);
            """;
        AddParameter(insertHistoryCommand, "$migration_id", InitialMigrationId);
        AddParameter(insertHistoryCommand, "$product_version", EfCoreProductVersion);

        await insertHistoryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> EnsureCreatedEfTablesExistAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var tableName in new[]
        {
            "device_definitions_ef",
            "device_definition_capabilities_ef",
            "device_definition_commands_ef",
            "device_instances_ef"
        })
        {
            if (!await TableExistsAsync(connection, tableName, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static async ValueTask<bool> MigrationHistoryContainsInitialMigrationAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = $migration_id;
            """;
        AddParameter(command, "$migration_id", InitialMigrationId);

        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
    }

    private static async ValueTask<bool> AddMissingDefinitionsAsync(
        DevicesDbContext context,
        IReadOnlyCollection<DeviceDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var definition in definitions)
        {
            var exists = await context.DeviceDefinitions
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == definition.Id, cancellationToken)
                .ConfigureAwait(false);
            if (exists)
            {
                continue;
            }

            context.DeviceDefinitions.Add(definition);
            changed = true;
        }

        return changed;
    }

    private static async ValueTask<bool> AddMissingInstancesAsync(
        DevicesDbContext context,
        IReadOnlyCollection<DeviceInstance> instances,
        CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var instance in instances)
        {
            var exists = await context.DeviceInstances
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == instance.Id, cancellationToken)
                .ConfigureAwait(false);
            if (exists)
            {
                continue;
            }

            context.DeviceInstances.Add(instance);
            changed = true;
        }

        return changed;
    }

    private static async ValueTask<IReadOnlyCollection<DeviceDefinition>> ReadSnapshotDefinitionsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "device_definitions", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM device_definitions
            ORDER BY definition_id;
            """;

        var definitions = new List<DeviceDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = JsonSerializer.Deserialize<PersistedDeviceDefinition>(
                reader.GetString(0),
                JsonOptions)
                ?? throw new InvalidOperationException("Persisted device definition document is empty.");
            definitions.Add(DevicePersistenceMapper.ToAggregate(snapshot));
        }

        return definitions;
    }

    private static async ValueTask<IReadOnlyCollection<DeviceInstance>> ReadSnapshotInstancesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "device_instances", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_json
            FROM device_instances
            ORDER BY instance_id;
            """;

        var instances = new List<DeviceInstance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = JsonSerializer.Deserialize<PersistedDeviceInstance>(
                reader.GetString(0),
                JsonOptions)
                ?? throw new InvalidOperationException("Persisted device instance document is empty.");
            instances.Add(DevicePersistenceMapper.ToAggregate(snapshot));
        }

        return instances;
    }

    private static async ValueTask<bool> TableExistsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM sqlite_master
            WHERE type = 'table'
                AND name = $table_name;
            """;

        AddParameter(command, "$table_name", tableName);

        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
