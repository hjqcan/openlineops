namespace OpenLineOps.Projects.Api.Models;

public sealed record CreateAutomationProjectRequest(
    string? ProjectId,
    string? DisplayName,
    string? ProjectPath);

public sealed record CreateAutomationProjectWorkspaceRequest(
    string? ProjectId,
    string? DisplayName,
    string? ProjectPath,
    string? DefaultApplicationId,
    string? DefaultApplicationName);

public sealed record OpenAutomationProjectWorkspaceRequest(string? ProjectPath);

public sealed record AddProjectApplicationRequest(
    string? ApplicationId,
    string? DisplayName);

public sealed record LinkProjectTopologyRequest(
    string? TopologyId);

public sealed record LinkProjectProcessDefinitionRequest(
    string? ProcessDefinitionId);

public sealed record PublishProjectSnapshotRequest(
    string? SnapshotId,
    string? ApplicationId,
    string? TopologyId,
    string? ProcessDefinitionId,
    string? ProcessVersionId,
    string? ConfigurationSnapshotId,
    IReadOnlyCollection<SnapshotCapabilityBindingRequest>? CapabilityBindings,
    IReadOnlyCollection<ProjectTargetReferenceRequest>? TargetReferences,
    IReadOnlyCollection<string>? BlockVersionIds);

public sealed record StartProjectSnapshotRuntimeSessionRequest(
    string? SerialNumber = null,
    string? BatchId = null,
    string? FixtureId = null,
    string? DeviceId = null,
    string? ActorId = null);

public sealed record SnapshotCapabilityBindingRequest(
    string? CapabilityId,
    string? BindingId,
    string? ProviderKind,
    string? ProviderKey);

public sealed record ProjectTargetReferenceRequest(
    string? Kind,
    string? TargetId);

public sealed record AutomationProjectResponse(
    string ProjectId,
    string DisplayName,
    string ProjectPath,
    DateTimeOffset CreatedAtUtc,
    string? ActiveSnapshotId,
    IReadOnlyCollection<ProjectApplicationResponse> Applications,
    IReadOnlyCollection<PublishedProjectSnapshotResponse> Snapshots);

public sealed record AutomationProjectSummaryResponse(
    string ProjectId,
    string DisplayName,
    string ProjectPath,
    string? ActiveSnapshotId);

public sealed record AutomationProjectWorkspaceResponse(
    AutomationProjectResponse Project,
    string ManifestPath,
    AutomationProjectManifestResponse Manifest);

public sealed record AutomationProjectManifestResponse(
    int FormatVersion,
    string Product,
    string ProjectId,
    string DisplayName,
    string ProjectPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ActiveSnapshotId,
    IReadOnlyCollection<ProjectApplicationManifestResponse> Applications,
    IReadOnlyCollection<PublishedProjectSnapshotManifestResponse> Snapshots);

public sealed record ProjectApplicationManifestResponse(
    string ApplicationId,
    string DisplayName,
    string? TopologyId,
    IReadOnlyCollection<string> ProcessDefinitionIds);

public sealed record ProjectApplicationResponse(
    string ApplicationId,
    string DisplayName,
    string? TopologyId,
    IReadOnlyCollection<string> ProcessDefinitionIds);

public sealed record PublishedProjectSnapshotResponse(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyCollection<SnapshotCapabilityBindingResponse> CapabilityBindings,
    IReadOnlyCollection<ProjectTargetReferenceResponse> TargetReferences,
    IReadOnlyCollection<string> BlockVersionIds);

public sealed record PublishedProjectSnapshotManifestResponse(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyCollection<SnapshotCapabilityBindingResponse> CapabilityBindings,
    IReadOnlyCollection<ProjectTargetReferenceResponse> TargetReferences,
    IReadOnlyCollection<string> BlockVersionIds);

public sealed record SnapshotCapabilityBindingResponse(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey);

public sealed record ProjectTargetReferenceResponse(
    string Kind,
    string TargetId);

public sealed record StartedProjectSnapshotRuntimeSessionResponse(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    Guid SessionId,
    string ConfigurationSnapshotId,
    string Status,
    int CompletedSteps,
    int CommandCount,
    int IncidentCount);
