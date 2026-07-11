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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitProjectSnapshotProductionRunRequest(
    Guid? ProductionRunId,
    string? ProductionUnitIdentityValue,
    string? ActorId,
    string? LotId = null,
    string? CarrierId = null,
    string? SlotId = null,
    string? FixtureId = null,
    string? DeviceId = null);

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
    string ProviderKey);

public sealed record ProjectTargetReferenceResponse(
    string Kind,
    string TargetId);

public sealed record SubmittedProjectSnapshotProductionRunResponse(
    string SnapshotId,
    string ProjectId,
    string ApplicationId,
    string TopologyId,
    string ProductionLineDefinitionId,
    Guid ProductionRunId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string ActorId,
    string? LotId,
    string? CarrierId,
    string ExecutionStatus,
    string Judgement,
    string Disposition,
    string ControlState,
    bool IsTerminal,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    IReadOnlyCollection<ProductionOperationRunResponse> Operations,
    IReadOnlyCollection<ProductionRouteDecisionResponse> RouteDecisions);

public sealed record ProductionOperationRunResponse(
    string OperationId,
    string OperationRunId,
    int Attempt,
    string StationSystemId,
    string RuntimeStationId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string ExecutionStatus,
    string Judgement,
    Guid? RuntimeSessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount);

public sealed record ProductionRouteDecisionResponse(
    string SourceOperationRunId,
    string TransitionId,
    string TargetOperationId,
    string SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);
