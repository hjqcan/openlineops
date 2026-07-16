namespace OpenLineOps.Plugins.Api.DependencyInjection;

public sealed class PluginsModuleOptions
{
    public const string SectionName = "OpenLineOps:Plugins";

    public string EventLogProvider { get; set; } = PluginEventLogProviders.Sqlite;

    public string? EventLogConnectionString { get; set; }

    public string EventLogDatabasePath { get; set; } = "data/openlineops-plugin-events.sqlite";

    public string PlatformVersion { get; set; } = "1.0.0";

    public string ContractVersion { get; set; } = "1.0.0";

    public bool RegisterRoutingInventories { get; set; }

    public string? ExternalHostExecutablePath { get; set; }

    public string? ExternalHostArgumentsTemplate { get; set; }

    public string ResolveEventLogConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(EventLogConnectionString))
        {
            return EventLogConnectionString.Trim();
        }

        var databasePath = Path.GetFullPath(EventLogDatabasePath);

        return $"Data Source={databasePath}";
    }
}

public static class PluginEventLogProviders
{
    public const string Sqlite = "Sqlite";

    public static PluginEventLogProvider Parse(string? value)
    {
        return value switch
        {
            Sqlite => PluginEventLogProvider.Sqlite,
            _ => throw new InvalidOperationException(
                $"Unsupported plugin event-log provider '{value}'. Expected exactly '{Sqlite}'.")
        };
    }
}

public enum PluginEventLogProvider
{
    Sqlite
}
