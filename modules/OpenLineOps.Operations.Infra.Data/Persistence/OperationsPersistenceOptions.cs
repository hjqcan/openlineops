namespace OpenLineOps.Operations.Infra.Data.Persistence;

public sealed class OperationsPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Operations:Persistence";

    public string Provider { get; set; } = OperationsPersistenceProviders.Sqlite;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = "data/openlineops-operations.sqlite";

    public string ResolveSqliteConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? $"Data Source={DatabasePath}"
            : ConnectionString.Trim();
    }

    public string ResolvePostgreSqlConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? throw new InvalidOperationException("PostgreSQL operations persistence requires ConnectionString.")
            : ConnectionString.Trim();
    }
}

public static class OperationsPersistenceProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";
    public const string PostgreSql = "PostgreSql";

    public static OperationsPersistenceProvider Parse(string? provider)
    {
        if (string.Equals(provider, Sqlite, StringComparison.Ordinal))
        {
            return OperationsPersistenceProvider.Sqlite;
        }

        if (string.Equals(provider, InMemory, StringComparison.Ordinal))
        {
            return OperationsPersistenceProvider.InMemory;
        }

        if (string.Equals(provider, PostgreSql, StringComparison.Ordinal))
        {
            return OperationsPersistenceProvider.PostgreSql;
        }

        throw new InvalidOperationException(
            $"Unsupported operations persistence provider '{provider}'. "
            + $"Expected exactly '{Sqlite}', '{InMemory}', or '{PostgreSql}'.");
    }
}

public enum OperationsPersistenceProvider
{
    Sqlite,
    InMemory,
    PostgreSql
}
