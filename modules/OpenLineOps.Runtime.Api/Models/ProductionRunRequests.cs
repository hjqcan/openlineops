namespace OpenLineOps.Runtime.Api.Models;

public sealed record CreateProductionRunRequest(
    Guid ProductionRunId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    RuntimeProductionUnitIdentityRequest ProductionUnitIdentity,
    string ActorId,
    string EntryOperationId,
    IReadOnlyList<ProductionOperationPlanRequest> Operations,
    IReadOnlyList<ProductionRouteTransitionRequest> RouteTransitions,
    string? LotId,
    string? CarrierId);

public sealed record RuntimeProductionUnitIdentityRequest(
    string ModelId,
    string InputKey,
    string Value);

public sealed record ProductionOperationPlanRequest(
    string OperationId,
    string StationSystemId,
    string RuntimeStationId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    IReadOnlyList<ProductionResourceRequirementRequest> Resources,
    RuntimeProcessRequest Process);

public sealed record ProductionResourceRequirementRequest(string Kind, string ResourceId);

public sealed record RuntimeProcessRequest(
    string? StartNodeId,
    IReadOnlyList<RuntimeProcessNodeRequest> Nodes,
    IReadOnlyList<RuntimeProcessRoutingNodeRequest> RoutingNodes,
    IReadOnlyList<RuntimeProcessTransitionRequest> Transitions);

public sealed record RuntimeProcessNodeRequest(
    string NodeId,
    string DisplayName,
    string TargetCapability,
    string CommandName,
    double TimeoutMilliseconds,
    string? InputPayload,
    string ActionId,
    string TargetKind,
    string TargetId);

public sealed record RuntimeProcessRoutingNodeRequest(
    string NodeId,
    string DisplayName,
    string Kind);

public sealed record RuntimeProcessTransitionRequest(
    string FromNodeId,
    string ToNodeId,
    string? Label,
    int? MaxTraversals);

public sealed record ProductionRouteTransitionRequest(
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

public sealed record ProductionRunCommandApiRequest(
    string ActorId,
    string? Reason,
    string? OperationId);
