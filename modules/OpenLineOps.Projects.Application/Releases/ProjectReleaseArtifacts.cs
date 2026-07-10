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
    IReadOnlyCollection<string> BlockVersionIds);

public sealed record ProjectReleaseCapabilityBinding(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey);

public sealed record ProjectReleaseTargetReference(
    string Kind,
    string TargetId);

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
    string ManifestPath,
    ProjectReleaseSourceMetadata Metadata,
    IReadOnlyCollection<ProjectReleaseSourceFile> Files);
