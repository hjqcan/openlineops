using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Application.Execution;

public static class ProjectReleaseRuntimeProviderKinds
{
    public const string Simulator = "Simulator";

    public const string DeviceInstance = "DeviceInstance";

    public const string PluginCommand = "PluginCommand";

    public const string ExternalSystem = "ExternalSystem";

    public const string ProcessCommandProvider = "ProcessCommandProvider";
}

public static class ProjectReleaseExternalTestProgramLaunchKinds
{
    public const string ApplicationExecutable = "ApplicationExecutable";

    public const string Provider = "Provider";
}

public abstract record ProjectReleaseRuntimeCommandRoute(
    string ProviderKind,
    string ProviderKey,
    DeviceCapabilityId CapabilityId);

public sealed record ProjectReleaseProcessCommandRoute(
    string ProviderKey,
    DeviceCapabilityId CapabilityId)
    : ProjectReleaseRuntimeCommandRoute(
        ProjectReleaseRuntimeProviderKinds.ProcessCommandProvider,
        ProviderKey,
        CapabilityId);

public sealed record ProjectReleaseDeviceCommandRoute(
    string ProviderKind,
    string ProviderKey,
    DeviceInstanceId DeviceInstanceId,
    DeviceCommandDefinitionId CommandDefinitionId,
    DeviceCapabilityId CapabilityId,
    DevicePluginPackageIdentity? PluginPackage = null)
    : ProjectReleaseRuntimeCommandRoute(ProviderKind, ProviderKey, CapabilityId);

public sealed record ExternalTestProgramRouteInputMapping(
    string Source,
    string Target);

public sealed record ExternalTestProgramRouteResultMapping(
    string SourcePath,
    string TargetKey);

public sealed record ExternalTestProgramRouteOutcomeMapping(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);

public sealed record ProjectReleaseExternalTestProgramCommandRoute(
    string ProviderKind,
    string ProviderKey,
    DeviceCapabilityId CapabilityId,
    string AdapterId,
    string LaunchKind,
    string ReleaseApplicationRootPath,
    string DutModelId,
    string DutModelCode,
    string DutIdentityInputKey,
    string? Executable,
    long? ExecutableSizeBytes,
    string? ExecutableSha256,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalTestProgramRouteInputMapping> InputMappings,
    IReadOnlyCollection<ExternalTestProgramRouteResultMapping> ResultMappings,
    ExternalTestProgramRouteOutcomeMapping OutcomeMapping,
    long TimeoutMilliseconds,
    ProjectReleaseRuntimeCommandRoute? ProviderRoute)
    : ProjectReleaseRuntimeCommandRoute(ProviderKind, ProviderKey, CapabilityId);

public sealed record DevicePluginPackageIdentity(
    string PluginId,
    string Version,
    string PackageContentSha256,
    string ManifestSha256,
    string EntryAssemblySha256,
    string ContractVersion,
    string RuntimeIdentifier,
    string AbiVersion);
