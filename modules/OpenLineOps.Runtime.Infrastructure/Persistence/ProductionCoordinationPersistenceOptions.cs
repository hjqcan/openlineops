namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class ProductionCoordinationPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Runtime:Coordination";

    public string Provider { get; set; } = ProductionCoordinationPersistenceProviders.PostgreSql;

    public string? ConnectionString { get; set; }

    public string? SqliteDatabasePath { get; set; }

    public string ResolvePostgreSqlConnectionString() => Canonical(ConnectionString, "ConnectionString");

    public string ResolveSqliteConnectionString() => ConnectionString is not null
        ? Canonical(ConnectionString, "ConnectionString")
        : $"Data Source={Canonical(SqliteDatabasePath, "SqliteDatabasePath")}";

    private static string Canonical(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidOperationException(
                $"Production coordination {name} must be canonical non-empty text.")
            : value;
}

public static class ProductionCoordinationPersistenceProviders
{
    public const string PostgreSql = "PostgreSql";
    public const string Sqlite = "Sqlite";
    public const string InMemory = "InMemory";

    public static ProductionCoordinationPersistenceProvider Parse(string? value) => value switch
    {
        PostgreSql => ProductionCoordinationPersistenceProvider.PostgreSql,
        Sqlite => ProductionCoordinationPersistenceProvider.Sqlite,
        InMemory => ProductionCoordinationPersistenceProvider.InMemory,
        _ => throw new InvalidOperationException(
            $"Unsupported Production coordination provider '{value}'. Expected exactly "
            + $"'{PostgreSql}', '{Sqlite}', or '{InMemory}'.")
    };
}

public enum ProductionCoordinationPersistenceProvider
{
    PostgreSql,
    Sqlite,
    InMemory
}
