using Npgsql;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class PostgreSqlSchemaInitialization
{
    // PostgreSQL serializes this lock per database, so independent repository instances and
    // application processes cannot race while creating their shared Runtime schema.
    private const long RuntimeSchemaLockId = 0x4F4C4F505343484D;

    public static async ValueTask AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@lock_id);";
        command.Parameters.AddWithValue("lock_id", RuntimeSchemaLockId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
