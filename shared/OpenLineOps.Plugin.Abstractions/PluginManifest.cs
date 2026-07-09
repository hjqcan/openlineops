namespace OpenLineOps.Plugin.Abstractions;

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    PluginKind Kind,
    string EntryAssembly,
    string EntryType,
    IReadOnlyCollection<string> Capabilities,
    string ContractVersion = "1.0.0",
    string MinimumPlatformVersion = "1.0.0",
    IReadOnlyCollection<PluginDeviceCommandDefinition>? DeviceCommands = null,
    IReadOnlyCollection<PluginProcessCommandDefinition>? ProcessCommands = null);
