namespace OpenLineOps.StationRuntime;

public sealed record StationRuntimeHostOptions(string PluginHostExecutablePath)
{
    public const string PluginHostExecutablePathEnvironmentVariable =
        "OpenLineOps__Plugins__ExternalHost__ExecutablePath";

    public static StationRuntimeHostOptions LoadFromEnvironment()
    {
        return new StationRuntimeHostOptions(
            Environment.GetEnvironmentVariable(PluginHostExecutablePathEnvironmentVariable)
            ?? string.Empty);
    }

    public StationRuntimeHostOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(PluginHostExecutablePath)
            || char.IsWhiteSpace(PluginHostExecutablePath[0])
            || char.IsWhiteSpace(PluginHostExecutablePath[^1])
            || !Path.IsPathFullyQualified(PluginHostExecutablePath))
        {
            throw new InvalidDataException(
                "Station Runtime requires a canonical absolute bundled Plugin Host executable path.");
        }

        var fullPath = Path.GetFullPath(PluginHostExecutablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "Bundled OpenLineOps Plugin Host executable does not exist.",
                fullPath);
        }

        return this with { PluginHostExecutablePath = fullPath };
    }
}
