namespace OpenLineOps.Projects.Application.Projects;

public sealed record CreateAutomationProjectRequest(
    string ProjectId,
    string DisplayName,
    string ProjectPath);

public sealed record AddProjectApplicationRequest(
    string ApplicationId,
    string DisplayName);

public sealed record LinkProjectTopologyRequest(
    string ApplicationId,
    string TopologyId);

public sealed record LinkProjectProcessDefinitionRequest(
    string ApplicationId,
    string ProcessDefinitionId);

public sealed record PublishProjectSnapshotRequest(
    string SnapshotId,
    string ApplicationId,
    string TopologyId,
    IReadOnlyCollection<string> LayoutIds,
    string ProductionLineDefinitionId,
    IReadOnlyCollection<SnapshotCapabilityBindingRequest> CapabilityBindings,
    IReadOnlyCollection<ProjectTargetReferenceRequest> TargetReferences,
    IReadOnlyCollection<string> BlockVersionIds,
    string ReleaseManifestPath,
    string ReleaseContentSha256);

public sealed record SnapshotCapabilityBindingRequest(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey,
    string OwnerSystemId,
    string OwnerStationSystemId);

public sealed record ProjectTargetReferenceRequest(
    string Kind,
    string TargetId);
