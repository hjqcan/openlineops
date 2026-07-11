using System.Text.Json.Serialization;

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

public sealed record ImportProjectApplicationRequest(string ProjectFilePath);

public sealed record AddProjectApplicationRequest(
    string? ApplicationId,
    string? DisplayName);

public sealed record LinkProjectTopologyRequest(
    string? TopologyId);

public sealed record LinkProjectProcessDefinitionRequest(
    string? ProcessDefinitionId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishProjectSnapshotRequest(
    string? SnapshotId,
    string? ApplicationId,
    string? ProductionLineDefinitionId);

public sealed record SnapshotCapabilityBindingRequest(
    string? CapabilityId,
    string? BindingId,
    string? ProviderKind,
    string? ProviderKey,
    string? OwnerSystemId,
    string? OwnerStationSystemId);

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
    IReadOnlyCollection<string> ProcessDefinitionIds,
    string ProjectFilePath);

public sealed record ProjectApplicationResponse(
    string ApplicationId,
    string DisplayName,
    string? TopologyId,
    IReadOnlyCollection<string> ProcessDefinitionIds,
    string ProjectFilePath);

public sealed record PublishedProjectSnapshotResponse(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    IReadOnlyCollection<string> LayoutIds,
    string ProductionLineDefinitionId,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyCollection<SnapshotCapabilityBindingResponse> CapabilityBindings,
    IReadOnlyCollection<ProjectTargetReferenceResponse> TargetReferences,
    IReadOnlyCollection<string> BlockVersionIds,
    string ReleaseManifestPath,
    string ReleaseContentSha256);

public sealed record PublishedProjectSnapshotManifestResponse(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    IReadOnlyCollection<string> LayoutIds,
    string ProductionLineDefinitionId,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyCollection<SnapshotCapabilityBindingResponse> CapabilityBindings,
    IReadOnlyCollection<ProjectTargetReferenceResponse> TargetReferences,
    IReadOnlyCollection<string> BlockVersionIds,
    string ReleaseManifestPath,
    string ReleaseContentSha256);

public sealed record SnapshotCapabilityBindingResponse(
    string CapabilityId,
    string BindingId,
    string ProviderKind,
    string ProviderKey,
    string OwnerSystemId,
    string OwnerStationSystemId);

public sealed record ProjectTargetReferenceResponse(
    string Kind,
    string TargetId);

public sealed record ProjectReleaseProductionRunContextResponse(
    string ProjectId,
    string ApplicationId,
    string SnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    string ProductModelId,
    string ProductModelIdentityInputKey,
    string EntryOperationId,
    string EntryStationSystemId,
    IReadOnlyCollection<string> StationSystemIds);
