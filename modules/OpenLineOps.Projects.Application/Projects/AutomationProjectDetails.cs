namespace OpenLineOps.Projects.Application.Projects;

public sealed record AutomationProjectDetails(
    string ProjectId,
    string DisplayName,
    string ProjectPath,
    DateTimeOffset CreatedAtUtc,
    string? ActiveSnapshotId,
    IReadOnlyCollection<ProjectApplicationDetails> Applications,
    IReadOnlyCollection<PublishedProjectSnapshotDetails> Snapshots);

public sealed record AutomationProjectSummary(
    string ProjectId,
    string DisplayName,
    string ProjectPath,
    string? ActiveSnapshotId);

public sealed record ProjectApplicationDetails(
    string ApplicationId,
    string DisplayName,
    string? TopologyId,
    IReadOnlyCollection<string> ProcessDefinitionIds,
    string ProjectFilePath);

public sealed record PublishedProjectSnapshotDetails(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    IReadOnlyCollection<string> LayoutIds,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyCollection<SnapshotCapabilityBindingDetails> CapabilityBindings,
    IReadOnlyCollection<ProjectTargetReferenceDetails> TargetReferences,
    IReadOnlyCollection<string> BlockVersionIds,
    string ReleaseManifestPath,
    string ReleaseContentSha256);

public sealed record SnapshotCapabilityBindingDetails(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey);

public sealed record ProjectTargetReferenceDetails(
    string Kind,
    string TargetId);
