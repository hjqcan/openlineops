namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public sealed record AutomationProjectManifest(
    int FormatVersion,
    string Product,
    string ProjectId,
    string DisplayName,
    string ProjectPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ActiveSnapshotId,
    ProjectApplicationManifest[] Applications,
    PublishedProjectSnapshotManifest[] Snapshots)
{
    public const int CurrentFormatVersion = 1;

    public const string ProductName = "OpenLineOps";

    public const string FileName = "openlineops.project.json";
}

public sealed record ProjectApplicationManifest(
    string ApplicationId,
    string DisplayName,
    string? TopologyId,
    string[] ProcessDefinitionIds);

public sealed record PublishedProjectSnapshotManifest(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    DateTimeOffset PublishedAtUtc,
    SnapshotCapabilityBindingManifest[] CapabilityBindings,
    ProjectTargetReferenceManifest[] TargetReferences,
    string[] BlockVersionIds);

public sealed record SnapshotCapabilityBindingManifest(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey);

public sealed record ProjectTargetReferenceManifest(
    string Kind,
    string TargetId);
