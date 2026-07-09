namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class ProcessDefinitionPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Processes:Persistence";

    public string Provider { get; set; } = ProcessDefinitionPersistenceProviders.InMemory;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = "data/openlineops-processes.sqlite";

    public string ResolveSqliteConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? $"Data Source={DatabasePath}"
            : ConnectionString.Trim();
    }

    public string ResolvePostgreSqlConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? throw new InvalidOperationException("PostgreSQL process definition persistence requires ConnectionString.")
            : ConnectionString.Trim();
    }
}

public static class ProcessDefinitionPersistenceProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";
    public const string PostgreSql = "PostgreSql";
}
