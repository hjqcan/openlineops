namespace OpenLineOps.Plugins.Application.Compatibility;

public sealed record PluginCompatibilityOptions(string PlatformVersion, string ContractVersion)
{
    public static PluginCompatibilityOptions Default { get; } = new("1.0.0", "1.0.0");
}
