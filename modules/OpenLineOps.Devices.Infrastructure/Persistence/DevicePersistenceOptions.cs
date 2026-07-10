namespace OpenLineOps.Devices.Infrastructure.Persistence;

public sealed class DevicePersistenceOptions
{
    public const string SectionName = "OpenLineOps:Devices:Persistence";

    public string Provider { get; set; } = DevicePersistenceProviders.Sqlite;

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

    public static DevicePersistenceProvider Parse(string? provider)
    {
        if (string.Equals(provider, Sqlite, StringComparison.Ordinal))
        {
            return DevicePersistenceProvider.Sqlite;
        }

        if (string.Equals(provider, InMemory, StringComparison.Ordinal))
        {
            return DevicePersistenceProvider.InMemory;
        }

        throw new InvalidOperationException(
            $"Unsupported device persistence provider '{provider}'. "
            + $"Expected exactly '{Sqlite}' or '{InMemory}'.");
    }
}

public enum DevicePersistenceProvider
{
    Sqlite,
    InMemory
}
