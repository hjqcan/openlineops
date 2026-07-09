namespace OpenLineOps.Plugins.Api.Models;

public sealed record PluginManagementOverviewResponse(
    IReadOnlyCollection<PluginPackageResponse> Packages,
    IReadOnlyCollection<PluginCapabilityResponse> Capabilities,
    IReadOnlyCollection<PluginCommandResponse> DeviceCommands,
    IReadOnlyCollection<PluginCommandResponse> ProcessCommands,
    IReadOnlyCollection<ExternalPluginProcessEventResponse> RecentEvents);

public sealed record PluginPackageResponse(
    PluginManifestResponse Manifest,
    string PackagePath,
    string ManifestPath,
    bool IsValid,
    IReadOnlyCollection<PluginValidationIssueResponse> ValidationIssues);

public sealed record PluginManifestResponse(
    string Id,
    string Name,
    string Version,
    string Kind,
    string EntryAssembly,
    string EntryType,
    string ContractVersion,
    string MinimumPlatformVersion,
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

public sealed record PluginValidationIssueResponse(
    string Code,
    string Message);

public sealed record PluginCapabilityResponse(
    string PluginId,
    string PluginName,
    string PluginKind,
    string Capability);

public sealed record PluginCommandResponse(
    string PluginId,
    string PluginName,
    string PluginKind,
    string CommandDefinitionId,
    string Capability,
    string CommandName,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutMilliseconds,
    int MaxRetries);

public sealed record PluginLifecycleRecordResponse(
    PluginManifestResponse Manifest,
    string State,
    string InitializationStatus,
    IReadOnlyCollection<PluginValidationIssueResponse> ValidationIssues,
    string? FailureReason);

public sealed record ExternalPluginProcessEventResponse(
    string Kind,
    string PluginId,
    string Message,
    DateTimeOffset OccurredAtUtc,
    string? Detail);
