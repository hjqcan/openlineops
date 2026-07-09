namespace OpenLineOps.Devices.Infrastructure.Persistence;

public sealed class DevicePersistenceOptions
{
    public const string SectionName = "OpenLineOps:Devices:Persistence";

    public string Provider { get; set; } = DevicePersistenceProviders.EfSqlite;

    public string? ConnectionString { get; set; }

    public string DatabasePath { get; set; } = "data/openlineops-devices.sqlite";

    public string ResolveSqliteConnectionString()
    {
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? $"Data Source={DatabasePath}"
            : ConnectionString.Trim();
    }
}

public static class DevicePersistenceProviders
{
    public const string InMemory = "InMemory";
    public const string Sqlite = "Sqlite";
    public const string EfSqlite = "EfSqlite";
}
