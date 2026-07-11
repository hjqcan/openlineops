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
}

public sealed record ProjectApplicationManifest(
    string ApplicationId,
    string DisplayName,
    string? TopologyId,
    string[] ProcessDefinitionIds,
    string ProjectFilePath);

public sealed record AutomationProjectFile(
    string SchemaVersion,
    int FormatVersion,
    string Kind,
    string Product,
    string ProjectId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ActiveSnapshotId,
    AutomationProjectApplicationReference[] Applications,
    AutomationProjectSnapshotFile[] Snapshots)
{
    public const string CurrentSchemaVersion = "openlineops.automation-project";

    public const string KindName = "OpenLineOps.AutomationProject";
}

public sealed record AutomationProjectApplicationReference(
    string ApplicationId,
    string ProjectFile);

public sealed record AutomationProjectSnapshotFile(
    string SnapshotId,
    string ApplicationId,
    string TopologyId,
    string[] LayoutIds,
    string ProductionLineDefinitionId,
    DateTimeOffset PublishedAtUtc,
    SnapshotCapabilityBindingManifest[] CapabilityBindings,
    ProjectTargetReferenceManifest[] TargetReferences,
    string[] BlockVersionIds,
    string ReleaseManifestPath,
    string ReleaseContentSha256);

public sealed record AutomationApplicationProjectFile(
    string SchemaVersion,
    int FormatVersion,
    string Kind,
    string Product,
    string ApplicationId,
    string DisplayName,
    int ResourceLayoutVersion,
    string? TopologyId,
    string[] ProcessDefinitionIds)
{
    public const string CurrentSchemaVersion = "openlineops.automation-application";

    public const int CurrentFormatVersion = 1;

    public const int CurrentResourceLayoutVersion = 1;

    public const string KindName = "OpenLineOps.AutomationApplication";
}

public sealed record PublishedProjectSnapshotManifest(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    string[] LayoutIds,
    string ProductionLineDefinitionId,
    DateTimeOffset PublishedAtUtc,
    SnapshotCapabilityBindingManifest[] CapabilityBindings,
    ProjectTargetReferenceManifest[] TargetReferences,
    string[] BlockVersionIds,
    string ReleaseManifestPath,
    string ReleaseContentSha256);

public sealed record SnapshotCapabilityBindingManifest(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey);

public sealed record ProjectTargetReferenceManifest(
    string Kind,
    string TargetId);
