namespace OpenLineOps.Devices.Api.DependencyInjection;

public static class DeviceCommandExecutorProviders
{
    public const string Fake = "Fake";

    public const string ConfiguredSimulator = "ConfiguredSimulator";

    public const string Plugin = "Plugin";

    public static bool IsFake(string value)
    {
        return string.Equals(value, Fake, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "DevelopmentFake", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsConfiguredSimulator(string value)
    {
        return string.Equals(value, ConfiguredSimulator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Config", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Simulator", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPlugin(string value)
    {
        return string.Equals(value, Plugin, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ExternalPlugin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "PluginProcess", StringComparison.OrdinalIgnoreCase);
    }
}
