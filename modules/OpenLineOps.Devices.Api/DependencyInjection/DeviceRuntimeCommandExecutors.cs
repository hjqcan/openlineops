namespace OpenLineOps.Devices.Api.DependencyInjection;

public static class DeviceRuntimeCommandExecutors
{
    public const string Simulator = "Simulator";

    public const string Device = "Device";

    public const string Plugin = "Plugin";

    public static bool IsDeviceBacked(string value)
    {
        return string.Equals(value, Device, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "DeviceBacked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Devices", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPlugin(string value)
    {
        return string.Equals(value, Plugin, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ExternalPlugin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "PluginProcess", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ProcessPlugin", StringComparison.OrdinalIgnoreCase);
    }
}
