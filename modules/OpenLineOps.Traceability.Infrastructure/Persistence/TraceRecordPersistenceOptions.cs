namespace OpenLineOps.Traceability.Infrastructure.Persistence;

public sealed class TraceRecordPersistenceOptions
{
    public const string SectionName = "OpenLineOps:Traceability:Persistence";

    public string Provider { get; set; } = TraceRecordPersistenceProviders.Sqlite;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = "data/openlineops-traceability.sqlite";

    public string ResolveSqliteConnectionString()
    {
        if (ConnectionString is not null)
        {
            return IsCanonical(ConnectionString)
                ? ConnectionString
                : throw new InvalidOperationException(
                    "SQLite traceability ConnectionString must be a non-empty canonical string.");
        }

        return IsCanonical(DatabasePath)
            ? $"Data Source={DatabasePath}"
            : throw new InvalidOperationException(
                "SQLite traceability DatabasePath must be a non-empty canonical string.");
    }

    private static bool IsCanonical(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[^1]);
    }
}

public static class TraceRecordPersistenceProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";

    public static TraceRecordPersistenceProvider Parse(string? provider)
    {
        if (string.Equals(provider, Sqlite, StringComparison.Ordinal))
        {
            return TraceRecordPersistenceProvider.Sqlite;
        }

        if (string.Equals(provider, InMemory, StringComparison.Ordinal))
        {
            return TraceRecordPersistenceProvider.InMemory;
        }

        throw new InvalidOperationException(
            $"Unsupported traceability persistence provider '{provider}'. "
            + $"Expected exactly '{Sqlite}' or '{InMemory}'.");
    }
}

public enum TraceRecordPersistenceProvider
{
    Sqlite,
    InMemory
}
