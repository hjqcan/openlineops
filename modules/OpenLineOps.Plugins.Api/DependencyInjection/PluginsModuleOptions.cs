namespace OpenLineOps.Plugins.Api.DependencyInjection;

public sealed class PluginsModuleOptions
{
    public const string SectionName = "OpenLineOps:Plugins";

    public string PackageRoot { get; set; } = string.Empty;

    public string[] ManifestFileNames { get; set; } =
    [
        "openlineops-plugin.json",
        "plugin.manifest.json",
        "plugin.json",
        "manifest.json"
    ];

    public string Activator { get; set; } = PluginActivators.ManifestOnly;

    public string EventLogProvider { get; set; } = PluginEventLogProviders.Sqlite;

    public string? EventLogConnectionString { get; set; }

    public string EventLogDatabasePath { get; set; } = "data/openlineops-plugin-events.sqlite";

    public string PlatformVersion { get; set; } = "1.0.0";

    public string ContractVersion { get; set; } = "1.0.0";

    public bool RegisterRoutingInventories { get; set; }

    public string? ExternalHostExecutablePath { get; set; }

    public string? ExternalHostArgumentsTemplate { get; set; }

    public string ResolvePackageRoot()
    {
        if (string.IsNullOrWhiteSpace(PackageRoot))
        {
            return PluginPathDefaults.ResolveDefaultPluginPackageRoot();
        }

        return Path.IsPathRooted(PackageRoot)
            ? Path.GetFullPath(PackageRoot)
            : Path.GetFullPath(Path.Combine(
                PluginPathDefaults.ResolveRepositoryRoot(),
                PackageRoot));
    }

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

public static class PluginActivators
{
    public const string ManifestOnly = "ManifestOnly";
    public const string AssemblyLoadContext = "AssemblyLoadContext";
    public const string ExternalProcess = "ExternalProcess";
}

public static class PluginEventLogProviders
{
    public const string Sqlite = "Sqlite";
    public const string None = "None";
}

internal static class PluginPathDefaults
{
    public static string ResolveDefaultPluginPackageRoot()
    {
        return Path.Combine(ResolveRepositoryRoot(), "samples", "plugins");
    }

    public static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var samplesPlugins = Path.Combine(current.FullName, "samples", "plugins");
            if (Directory.Exists(samplesPlugins))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
