namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class RuntimeSessionPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Runtime:Persistence";

    public string Provider { get; set; } = RuntimeSessionPersistenceProviders.Sqlite;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = "data/openlineops-runtime.sqlite";

    public string ResolveSqliteConnectionString()
    {
        if (ConnectionString is not null)
        {
            return IsCanonical(ConnectionString)
                ? ConnectionString
                : throw new InvalidOperationException(
                    "SQLite Runtime ConnectionString must be a non-empty canonical string.");
        }

        return IsCanonical(DatabasePath)
            ? $"Data Source={DatabasePath}"
            : throw new InvalidOperationException(
                "SQLite Runtime DatabasePath must be a non-empty canonical string.");
    }

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1]);
}

public static class RuntimeSessionPersistenceProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";

    public static RuntimeSessionPersistenceProvider Parse(string? provider)
    {
        if (string.Equals(provider, Sqlite, StringComparison.Ordinal))
        {
            return RuntimeSessionPersistenceProvider.Sqlite;
        }

        if (string.Equals(provider, InMemory, StringComparison.Ordinal))
        {
            return RuntimeSessionPersistenceProvider.InMemory;
        }

        throw new InvalidOperationException(
            $"Unsupported runtime session persistence provider '{provider}'. "
            + $"Expected exactly '{Sqlite}' or '{InMemory}'.");
    }
}

public enum RuntimeSessionPersistenceProvider
{
    Sqlite,
    InMemory
}
