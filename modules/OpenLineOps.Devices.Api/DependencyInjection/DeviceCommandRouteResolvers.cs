namespace OpenLineOps.Devices.Api.DependencyInjection;

public static class DeviceCommandRouteResolvers
{
    public const string Engineering = "Engineering";

    public const string Static = "Static";

    public static bool IsStatic(string value)
    {
        return string.Equals(value, Static, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEngineering(string value)
    {
        return string.Equals(value, Engineering, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ConfigurationSnapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "EngineeringSnapshot", StringComparison.OrdinalIgnoreCase);
    }
}
