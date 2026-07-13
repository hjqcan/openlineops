using System.Collections.Concurrent;
using System.Data;
using Npgsql;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

internal static class PostgreSqlOperationsSchema
{
    private const long OperationsSchemaLockId = 0x4F4C4F504F505343;

    private static readonly ConcurrentDictionary<SchemaIdentity, SchemaInitializationGate>
        InitializationGates = new();

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS operations_alarms (
            "Id" character varying(160) NOT NULL,
            "StationId" character varying(160) NOT NULL,
            "Source" character varying(160) NOT NULL,
            "SourceId" character varying(160) NULL,
            "Severity" character varying(32) NOT NULL,
            "Status" character varying(32) NOT NULL,
            "Title" character varying(200) NOT NULL,
            "Description" character varying(1000) NOT NULL,
            "RaisedAtUtc" bigint NOT NULL,
            "AcknowledgedBy" character varying(160) NULL,
            "AcknowledgedAtUtc" bigint NULL,
            "ResolvedBy" character varying(160) NULL,
            "ResolvedAtUtc" bigint NULL,
            "ResolutionNote" character varying(1000) NULL,
            CONSTRAINT "PK_operations_alarms" PRIMARY KEY ("Id")
        );

        CREATE INDEX IF NOT EXISTS "IX_operations_alarms_RaisedAtUtc"
            ON operations_alarms ("RaisedAtUtc");
        CREATE INDEX IF NOT EXISTS "IX_operations_alarms_StationId_Status"
            ON operations_alarms ("StationId", "Status");
        """;

    private static readonly ExpectedColumn[] ExpectedColumns =
    [
        new("Id", "varchar", false, 160),
        new("StationId", "varchar", false, 160),
        new("Source", "varchar", false, 160),
        new("SourceId", "varchar", true, 160),
        new("Severity", "varchar", false, 32),
        new("Status", "varchar", false, 32),
        new("Title", "varchar", false, 200),
        new("Description", "varchar", false, 1000),
        new("RaisedAtUtc", "int8", false, null),
        new("AcknowledgedBy", "varchar", true, 160),
        new("AcknowledgedAtUtc", "int8", true, null),
        new("ResolvedBy", "varchar", true, 160),
        new("ResolvedAtUtc", "int8", true, null),
        new("ResolutionNote", "varchar", true, 1000)
    ];

    private static readonly ExpectedPrimaryKeyColumn[] ExpectedPrimaryKeyColumns =
    [
        new("PK_operations_alarms", "Id", 1)
    ];

    private static readonly ExpectedIndexColumn[] ExpectedIndexColumns =
    [
        new(
            "IX_operations_alarms_RaisedAtUtc",
            "btree",
            false,
            true,
            true,
            1,
            1,
            null,
            null,
            "RaisedAtUtc",
            1,
            true),
        new(
            "IX_operations_alarms_StationId_Status",
            "btree",
            false,
            true,
            true,
            2,
            2,
            null,
            null,
            "StationId",
            1,
            true),
        new(
            "IX_operations_alarms_StationId_Status",
            "btree",
            false,
            true,
            true,
            2,
            2,
            null,
            null,
            "Status",
            2,
            true)
    ];

    public static void EnsureReady(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var gate = InitializationGates.GetOrAdd(
            CreateIdentity(connection),
            static _ => new SchemaInitializationGate());
        if (gate.IsReady)
        {
            return;
        }

        gate.Mutex.Wait();
        try
        {
            if (gate.IsReady)
            {
                return;
            }

            EnsureReadyCore(connection);
            gate.MarkReady();
        }
        finally
        {
            gate.Mutex.Release();
        }
    }

    public static async Task EnsureReadyAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var gate = InitializationGates.GetOrAdd(
            CreateIdentity(connection),
            static _ => new SchemaInitializationGate());
        if (gate.IsReady)
        {
            return;
        }

        await gate.Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (gate.IsReady)
            {
                return;
            }

            await EnsureReadyCoreAsync(connection, cancellationToken).ConfigureAwait(false);
            gate.MarkReady();
        }
        finally
        {
            gate.Mutex.Release();
        }
    }

    private static void EnsureReadyCore(NpgsqlConnection connection)
    {
        var closeConnection = connection.State == ConnectionState.Closed;
        if (closeConnection)
        {
            connection.Open();
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            AcquireLock(connection, transaction);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = SchemaSql;
                command.ExecuteNonQuery();
            }

            Validate(connection, transaction);
            transaction.Commit();
        }
        finally
        {
            if (closeConnection)
            {
                connection.Close();
            }
        }
    }

    private static async Task EnsureReadyCoreAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var closeConnection = connection.State == ConnectionState.Closed;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await AcquireLockAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ValidateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static SchemaIdentity CreateIdentity(NpgsqlConnection connection)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        return new SchemaIdentity(
            settings.Host ?? string.Empty,
            settings.Port,
            settings.Database ?? string.Empty,
            settings.Username ?? string.Empty,
            settings.SearchPath ?? string.Empty);
    }

    private static void AcquireLock(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@lock_id);";
        command.Parameters.AddWithValue("lock_id", OperationsSchemaLockId);
        command.ExecuteNonQuery();
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@lock_id);";
        command.Parameters.AddWithValue("lock_id", OperationsSchemaLockId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = CreateValidationCommand(connection, transaction);
        var actual = new List<ExpectedColumn>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                actual.Add(new ExpectedColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    string.Equals(reader.GetString(2), "YES", StringComparison.Ordinal),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3)));
            }
        }

        RequireExpectedColumns(actual);
        ValidatePrimaryKey(connection, transaction);
        ValidateIndexes(connection, transaction);
    }

    private static async Task ValidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateValidationCommand(connection, transaction);
        var actual = new List<ExpectedColumn>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                actual.Add(new ExpectedColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    string.Equals(reader.GetString(2), "YES", StringComparison.Ordinal),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3)));
            }
        }

        RequireExpectedColumns(actual);
        await ValidatePrimaryKeyAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await ValidateIndexesAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    private static NpgsqlCommand CreateValidationCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT column_name, udt_name, is_nullable, character_maximum_length
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = 'operations_alarms'
            ORDER BY ordinal_position;
            """;
        return command;
    }

    private static void RequireExpectedColumns(IReadOnlyCollection<ExpectedColumn> actual)
    {
        if (!actual.SequenceEqual(ExpectedColumns))
        {
            throw new InvalidDataException(
                "PostgreSQL table operations_alarms does not match the only supported Operations schema.");
        }
    }

    private static void ValidatePrimaryKey(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = CreatePrimaryKeyValidationCommand(connection, transaction);
        using var reader = command.ExecuteReader();
        var actual = new List<ExpectedPrimaryKeyColumn>();
        while (reader.Read())
        {
            actual.Add(new ExpectedPrimaryKeyColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }

        RequireExpectedPrimaryKey(actual);
    }

    private static async Task ValidatePrimaryKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreatePrimaryKeyValidationCommand(connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var actual = new List<ExpectedPrimaryKeyColumn>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add(new ExpectedPrimaryKeyColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }

        RequireExpectedPrimaryKey(actual);
    }

    private static NpgsqlCommand CreatePrimaryKeyValidationCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT constraint_metadata.conname, column_metadata.attname, key_metadata.ordinality
            FROM pg_constraint AS constraint_metadata
            INNER JOIN pg_class AS table_metadata
                ON table_metadata.oid = constraint_metadata.conrelid
            INNER JOIN pg_namespace AS schema_metadata
                ON schema_metadata.oid = table_metadata.relnamespace
            CROSS JOIN LATERAL unnest(constraint_metadata.conkey)
                WITH ORDINALITY AS key_metadata(attnum, ordinality)
            INNER JOIN pg_attribute AS column_metadata
                ON column_metadata.attrelid = table_metadata.oid
                AND column_metadata.attnum = key_metadata.attnum
            WHERE schema_metadata.nspname = current_schema()
              AND table_metadata.relname = 'operations_alarms'
              AND constraint_metadata.contype = 'p'
            ORDER BY constraint_metadata.conname, key_metadata.ordinality;
            """;
        return command;
    }

    private static void RequireExpectedPrimaryKey(
        IReadOnlyCollection<ExpectedPrimaryKeyColumn> actual)
    {
        if (!actual.SequenceEqual(ExpectedPrimaryKeyColumns))
        {
            throw new InvalidDataException(
                "PostgreSQL table operations_alarms does not have the required primary key.");
        }
    }

    private static void ValidateIndexes(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = CreateIndexValidationCommand(connection, transaction);
        using var reader = command.ExecuteReader();
        var actual = new List<ExpectedIndexColumn>();
        while (reader.Read())
        {
            actual.Add(ReadIndexColumn(reader));
        }

        RequireExpectedIndexes(actual);
    }

    private static async Task ValidateIndexesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateIndexValidationCommand(connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var actual = new List<ExpectedIndexColumn>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add(ReadIndexColumn(reader));
        }

        RequireExpectedIndexes(actual);
    }

    private static NpgsqlCommand CreateIndexValidationCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT index_name.relname,
                   access_method.amname,
                   index_metadata.indisunique,
                   index_metadata.indisvalid,
                   index_metadata.indisready,
                   index_metadata.indnkeyatts::integer,
                   index_metadata.indnatts::integer,
                   pg_get_expr(index_metadata.indpred, index_metadata.indrelid),
                   pg_get_expr(index_metadata.indexprs, index_metadata.indrelid),
                   column_metadata.attname,
                   key_metadata.ordinality::integer,
                   key_metadata.ordinality <= index_metadata.indnkeyatts
            FROM pg_index AS index_metadata
            INNER JOIN pg_class AS table_metadata
                ON table_metadata.oid = index_metadata.indrelid
            INNER JOIN pg_namespace AS schema_metadata
                ON schema_metadata.oid = table_metadata.relnamespace
            INNER JOIN pg_class AS index_name
                ON index_name.oid = index_metadata.indexrelid
            INNER JOIN pg_am AS access_method
                ON access_method.oid = index_name.relam
            CROSS JOIN LATERAL unnest(index_metadata.indkey)
                WITH ORDINALITY AS key_metadata(attnum, ordinality)
            LEFT JOIN pg_attribute AS column_metadata
                ON column_metadata.attrelid = table_metadata.oid
                AND column_metadata.attnum = key_metadata.attnum
            WHERE schema_metadata.nspname = current_schema()
              AND table_metadata.relname = 'operations_alarms'
              AND NOT index_metadata.indisprimary
            ORDER BY index_name.relname, key_metadata.ordinality;
            """;
        return command;
    }

    private static ExpectedIndexColumn ReadIndexColumn(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetBoolean(2),
        reader.GetBoolean(3),
        reader.GetBoolean(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetInt32(10),
        reader.GetBoolean(11));

    private static void RequireExpectedIndexes(IReadOnlyCollection<ExpectedIndexColumn> actual)
    {
        if (!actual.SequenceEqual(ExpectedIndexColumns))
        {
            throw new InvalidDataException(
                "PostgreSQL table operations_alarms does not have the only supported index set.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string StoreType,
        bool IsNullable,
        int? MaximumLength);

    private sealed record ExpectedPrimaryKeyColumn(
        string ConstraintName,
        string ColumnName,
        long Ordinal);

    private sealed record ExpectedIndexColumn(
        string IndexName,
        string AccessMethod,
        bool IsUnique,
        bool IsValid,
        bool IsReady,
        int KeyColumnCount,
        int TotalColumnCount,
        string? Predicate,
        string? Expressions,
        string? ColumnName,
        int Ordinal,
        bool IsKeyColumn);

    private sealed record SchemaIdentity(
        string Host,
        int Port,
        string Database,
        string Username,
        string SearchPath);

    private sealed class SchemaInitializationGate
    {
        private int _isReady;

        public SemaphoreSlim Mutex { get; } = new(1, 1);

        public bool IsReady => Volatile.Read(ref _isReady) == 1;

        public void MarkReady()
        {
            Volatile.Write(ref _isReady, 1);
        }
    }
}
