namespace OpenLineOps.Traceability.Infrastructure.Persistence;

public sealed class TraceRecordPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Traceability:Persistence";

    public string Provider { get; set; } = TraceRecordPersistenceProviders.InMemory;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = "data/openlineops-traceability.sqlite";

    public string ResolveSqliteConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? $"Data Source={DatabasePath}"
            : ConnectionString.Trim();
    }

    public string ResolvePostgreSqlConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? throw new InvalidOperationException("PostgreSQL traceability persistence requires ConnectionString.")
            : ConnectionString.Trim();
    }
}

public static class TraceRecordPersistenceProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";
    public const string PostgreSql = "PostgreSql";
}
