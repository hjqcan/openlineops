using System.Text.Json.Serialization;

namespace OpenLineOps.Projects.Application.Releases;

public sealed record ProjectReleaseSourceMetadata(
    string TopologyId,
    IReadOnlyCollection<string> LayoutIds,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string FlowIrSchemaVersion,
    string FlowIrSha256,
    string FlowIrCanonicalJson,
    string ConfigurationSnapshotId,
    IReadOnlyCollection<ProjectReleaseCapabilityBinding> CapabilityBindings,
    IReadOnlyCollection<ProjectReleaseTargetReference> TargetReferences,
    IReadOnlyCollection<string> BlockVersionIds,
    IReadOnlyCollection<ProjectReleasePackageDependencyLock> PackageDependencies);

public sealed record ProjectReleaseCapabilityBinding(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey);

public sealed record ProjectReleaseTargetReference(
    string Kind,
    string TargetId);

public sealed record ProjectReleasePackageDependencyLock(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey,
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
