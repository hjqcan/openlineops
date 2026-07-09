namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class RuntimeSessionPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Runtime:Persistence";

    public string Provider { get; set; } = RuntimeSessionPersistenceProviders.InMemory;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = "data/openlineops-runtime.sqlite";

    public string ResolveSqliteConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? $"Data Source={DatabasePath}"
            : ConnectionString.Trim();
    }

    public string ResolvePostgreSqlConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? throw new InvalidOperationException("PostgreSQL runtime session persistence requires ConnectionString.")
            : ConnectionString.Trim();
    }
}

public static class RuntimeSessionPersistenceProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";
    public const string PostgreSql = "PostgreSql";
}
