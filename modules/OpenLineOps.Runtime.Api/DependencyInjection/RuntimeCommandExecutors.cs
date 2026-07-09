namespace OpenLineOps.Runtime.Api.DependencyInjection;

public static class RuntimeCommandExecutors
{
    public const string Simulator = "Simulator";

    public const string Plugin = "Plugin";

    public static bool IsSimulator(string value)
    {
        return string.Equals(value, Simulator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Simulated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Fake", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPlugin(string value)
    {
        return string.Equals(value, Plugin, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ExternalPlugin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "PluginProcess", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ProcessPlugin", StringComparison.OrdinalIgnoreCase);
    }
}
