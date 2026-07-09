namespace OpenLineOps.Operations.Infra.Data.Persistence;

public sealed class OperationsPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Operations:Persistence";

    public string Provider { get; set; } = OperationsPersistenceProviders.EfSqlite;

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
    public const string EfSqlite = "EfSqlite";
    public const string PostgreSql = "PostgreSql";
}
