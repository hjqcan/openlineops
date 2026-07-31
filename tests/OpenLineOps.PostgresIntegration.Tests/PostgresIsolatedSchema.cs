using Npgsql;

namespace OpenLineOps.PostgresIntegration.Tests;

internal sealed class PostgresIsolatedSchema : IAsyncDisposable
{
    private readonly string _adminConnectionString;

    private PostgresIsolatedSchema(
        string adminConnectionString,
        string schemaName,
        string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        SchemaName = schemaName;
        ConnectionString = connectionString;
    }

    public string SchemaName { get; }

    public string ConnectionString { get; }

    public static async Task<PostgresIsolatedSchema> CreateAsync(
        string adminConnectionString,
        string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose)
            || purpose.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "PostgreSQL test schema purpose must be alphanumeric.",
                nameof(purpose));
        }

        var schemaName = $"olo_{purpose.ToLowerInvariant()}_{Guid.NewGuid():N}";
        await ExecuteAsync(
                adminConnectionString,
                $"CREATE SCHEMA \"{schemaName}\";")
            .ConfigureAwait(false);
        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            SearchPath = schemaName
        };
        return new PostgresIsolatedSchema(
            adminConnectionString,
            schemaName,
            builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsync(
                _adminConnectionString,
                $"DROP SCHEMA \"{SchemaName}\" CASCADE;")
            .ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
