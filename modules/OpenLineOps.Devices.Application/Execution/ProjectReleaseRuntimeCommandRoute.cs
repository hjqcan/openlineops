using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Devices.Application.Execution;

public static class ProjectReleaseRuntimeProviderKinds
{
    public const string Simulator = "Simulator";

    public const string DeviceInstance = "DeviceInstance";

    public const string PluginCommand = "PluginCommand";

    public const string ExternalSystem = "ExternalSystem";

    public const string ProcessCommandProvider = "ProcessCommandProvider";
}

public static class ProjectReleaseExternalProgramLaunchKinds
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

public sealed record ProjectReleaseLineControllerCommandRoute(
    string AuthorizationId,
    ProjectReleaseDeviceCommandRoute ControllerRoute,
    string TargetStationSystemId,
    string TargetSystemId,
    string TargetBindingId,
    string TargetCapabilityId,
    string TargetAction)
    : ProjectReleaseRuntimeCommandRoute(
        ControllerRoute.ProviderKind,
        ControllerRoute.ProviderKey,
        ControllerRoute.CapabilityId);

public sealed record ExternalProgramRouteInputMapping(string Source, string Target);

public sealed record ExternalProgramRouteResultMapping(
    string SourcePath,
    string TargetKey,
    ProductionContextValueKind ValueKind);

public sealed record ExternalProgramRouteOutcomeMapping(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);

public sealed record ExternalProgramRoutePermissionProfile(
    string ProfileName,
    bool NetworkAccessAllowed,
    IReadOnlyCollection<string> AllowedEnvironmentVariables);

public sealed record ExternalProgramRouteExecutionLimits(
    long TimeoutMilliseconds,
    int MaximumProcessCount,
    long MaximumWorkingSetBytes,
    long MaximumCpuTimeMilliseconds,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    int MaximumArtifactCount,
    long MaximumArtifactBytes,
    long MaximumTotalArtifactBytes);

public sealed record ExternalProgramRouteFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ProjectReleaseExternalProgramCommandRoute(
    string ProviderKind,
    string ProviderKey,
    DeviceCapabilityId CapabilityId,
    string ResourceId,
    string LaunchKind,
    string ReleaseApplicationRootPath,
    string ResourceRelativePath,
    string ProductModelId,
    string ProductModelCode,
    string ProductionUnitIdentityInputKey,
    string? EntryPoint,
    long? EntryPointSizeBytes,
    string? EntryPointSha256,
    IReadOnlyCollection<ExternalProgramRouteFile> Files,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalProgramRouteInputMapping> InputMappings,
    IReadOnlyCollection<ExternalProgramRouteResultMapping> ResultMappings,
    ExternalProgramRouteOutcomeMapping OutcomeMapping,
    ExternalProgramRoutePermissionProfile PermissionProfile,
    ExternalProgramRouteExecutionLimits ExecutionLimits,
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
