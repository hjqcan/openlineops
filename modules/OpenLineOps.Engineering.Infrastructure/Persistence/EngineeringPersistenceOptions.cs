namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class EngineeringPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Engineering:Persistence";

    public string Provider { get; set; } = EngineeringPersistenceProviders.InMemory;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = "data/openlineops-engineering.sqlite";

    public string ResolveSqliteConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? $"Data Source={DatabasePath}"
            : ConnectionString.Trim();
    }

    public string ResolvePostgreSqlConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? throw new InvalidOperationException("PostgreSQL engineering persistence requires ConnectionString.")
            : ConnectionString.Trim();
    }
}

public static class EngineeringPersistenceProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";
    public const string PostgreSql = "PostgreSql";
}
