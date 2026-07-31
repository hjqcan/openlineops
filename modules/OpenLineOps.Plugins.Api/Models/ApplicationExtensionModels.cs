namespace OpenLineOps.Plugins.Api.Models;

public sealed record ApplicationExtensionPackageResponse(
    string PortableId,
    string PluginId,
    string Version,
    string ManifestPath,
    string ContentSha256,
    string ManifestSha256,
    string EntryAssemblySha256,
    IReadOnlyCollection<ApplicationExtensionPackageFileResponse> Files,
    bool IsValid,
    IReadOnlyCollection<PluginValidationIssueResponse> ValidationIssues,
    PluginManifestResponse Manifest);

public sealed record ApplicationExtensionPackageFileResponse(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record PluginManifestResponse(
    string Id,
    string Name,
    string Version,
    string Kind,
    string EntryAssembly,
    string EntryType,
    string ContractVersion,
    string MinimumPlatformVersion,
    string RuntimeIdentifier,
    string AbiVersion,
    IReadOnlyCollection<string> Capabilities,
    IReadOnlyCollection<PluginCommandDefinitionResponse> DeviceCommands,
    IReadOnlyCollection<PluginCommandDefinitionResponse> ProcessCommands);

public sealed record PluginCommandDefinitionResponse(
    string Id,
    string Capability,
    string CommandName,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutMilliseconds,
    int MaxRetries);

public sealed record PluginValidationIssueResponse(string Code, string Message);
