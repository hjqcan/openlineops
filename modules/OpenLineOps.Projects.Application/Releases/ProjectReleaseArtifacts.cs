using System.Text.Json.Serialization;

namespace OpenLineOps.Projects.Application.Releases;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectReleaseManifest(
    string Schema,
    int SchemaVersion,
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    DateTimeOffset PublishedAtUtc,
    string SourceApplicationRelativePath,
    string ApplicationProjectRelativePath,
    ProjectReleaseSourceMetadata Metadata,
    ProjectReleaseSourceFile[] Files,
    string ContentSha256)
{
    public const string RequiredSchema = "openlineops.project-release-artifact";

    public const int RequiredSchemaVersion = 1;

    public const string FileName = "release.json";
}

public sealed record ProjectReleaseSourceMetadata(
    string TopologyId,
    IReadOnlyCollection<string> LayoutIds,
    ProjectReleaseProductionLine ProductionLine,
    IReadOnlyCollection<ProjectReleaseExternalProgramResource> ExternalProgramResources,
    IReadOnlyCollection<ProjectReleaseCapabilityBinding> CapabilityBindings,
    IReadOnlyCollection<ProjectReleaseTargetReference> TargetReferences,
    IReadOnlyCollection<string> BlockVersionIds,
    IReadOnlyCollection<ProjectReleasePackageDependencyLock> PackageDependencies);

public sealed record ProjectReleaseProductionLine(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    ProjectReleaseProductModel ProductModel,
    string EntryOperationId,
    IReadOnlyCollection<ProjectReleaseOperation> Operations,
    IReadOnlyCollection<ProjectReleaseRouteTransition> Transitions,
    IReadOnlyCollection<ProjectReleaseLineControllerAuthorization> LineControllerAuthorizations);

public sealed record ProjectReleaseProductModel(
    string ProductModelId,
    string ModelCode,
    string IdentityInputKey);

public sealed record ProjectReleaseOperation(
    string OperationId,
    string DisplayName,
    string StationSystemId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId,
    string FlowVersionId,
    string FlowIrSchema,
    string FlowIrSha256,
    string FlowIrCanonicalJson,
    IReadOnlyCollection<string> BlockVersionIds,
    IReadOnlyCollection<ProjectReleaseOperationResource> Resources,
    IReadOnlyCollection<ProjectReleaseAuthorizedAction> AuthorizedActions);

public sealed record ProjectReleaseOperationResource(
    string BindingId,
    string Kind,
    string TopologyTargetId,
    string Resolution,
    IReadOnlyCollection<string> EligibleSlotIds);

public sealed record ProjectReleaseAuthorizedAction(
    string ActionId,
    string NodeId,
    string Kind,
    string RequiredCapability,
    string CommandName,
    string TargetKind,
    string TargetId,
    long TimeoutMilliseconds,
    string? LineControllerAuthorizationId);

public sealed record ProjectReleaseLineControllerAuthorization(
    string AuthorizationId,
    string OperationId,
    string ActionId,
    string ControllerSystemId,
    string ControllerBindingId,
    string ControllerCapabilityId,
    string ControllerAction,
    string TargetStationSystemId,
    string TargetSystemId,
    string TargetBindingId,
    string TargetCapabilityId,
    string TargetAction);

public sealed record ProjectReleaseRouteTransition(
    string TransitionId,
    string SourceOperationId,
    string TargetOperationId,
    string Kind,
    string? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    string? ExpectedOutputKind,
    string? ExpectedOutputValue);

public sealed record ProjectReleaseExternalProgramResource(
    string ResourceId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    string LaunchKind,
    string? EntryPoint,
    string? ResourceProviderKind,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ProjectReleaseExternalProgramInputMapping> InputMappings,
    IReadOnlyCollection<ProjectReleaseExternalProgramResultMapping> ResultMappings,
    ProjectReleaseExternalProgramOutcomeMapping OutcomeMapping,
    ProjectReleaseExternalProgramPermissionProfile PermissionProfile,
    ProjectReleaseExternalProgramExecutionLimits ExecutionLimits,
    IReadOnlyCollection<ProjectReleaseExternalProgramFile> Files,
    string ContentSha256,
    string ResourceRelativePath);

public sealed record ProjectReleaseExternalProgramInputMapping(
    string Source,
    string Target);

public sealed record ProjectReleaseExternalProgramResultMapping(
    string SourcePath,
    string TargetKey,
    string ValueKind);

public sealed record ProjectReleaseExternalProgramOutcomeMapping(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);

public sealed record ProjectReleaseExternalProgramPermissionProfile(
    string ProfileName,
    bool NetworkAccessAllowed,
    IReadOnlyCollection<string> AllowedEnvironmentVariables);

public sealed record ProjectReleaseExternalProgramExecutionLimits(
    long TimeoutMilliseconds,
    int MaximumProcessCount,
    long MaximumWorkingSetBytes,
    long MaximumCpuTimeMilliseconds,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    int MaximumArtifactCount,
    long MaximumArtifactBytes,
    long MaximumTotalArtifactBytes);

public sealed record ProjectReleaseExternalProgramFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ProjectReleaseCapabilityBinding(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey,
    string OwnerSystemId,
    string OwnerStationSystemId);

public sealed record ProjectReleaseTargetReference(
    string Kind,
    string TargetId);

public sealed record ProjectReleasePackageDependencyLock(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey,
    string OwnerSystemId,
    string OwnerStationSystemId,
    string PackageId,
    string PluginId,
    string PackageVersion,
    string PackageContentSha256,
    string ManifestSha256,
    string EntryAssemblySha256,
    string ContractVersion,
    string RuntimeIdentifier,
    string AbiVersion,
    string PackageRelativePath,
    string ManifestRelativePath,
    string EntryAssemblyRelativePath,
    IReadOnlyCollection<ProjectReleasePackageCommandLock> Commands,
    IReadOnlyCollection<ProjectReleasePackageFile> Files,
    [property: JsonIgnore] string? SourcePackagePath = null);

public sealed record ProjectReleasePackageCommandLock(
    string Kind,
    string CommandDefinitionId,
    string CapabilityId,
    string CommandName);

public sealed record ProjectReleasePackageFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ProjectReleaseSourceFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ProjectReleaseArtifactDescriptor(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    DateTimeOffset PublishedAtUtc,
    string ContentSha256,
    string ReleaseRootPath,
    string SourceRootPath,
    string ApplicationProjectRelativePath,
    string ManifestPath,
    IReadOnlyCollection<ProjectReleaseSourceFile> Files);

public sealed record OpenedProjectReleaseArtifact(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    DateTimeOffset PublishedAtUtc,
    string ContentSha256,
    string ReleaseRootPath,
    string SourceRootPath,
    string ApplicationProjectRelativePath,
    string ManifestPath,
    ProjectReleaseSourceMetadata Metadata,
    IReadOnlyCollection<ProjectReleaseSourceFile> Files);
