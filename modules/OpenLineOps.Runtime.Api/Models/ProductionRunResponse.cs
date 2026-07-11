namespace OpenLineOps.Runtime.Api.Models;

public sealed record ProductionRunResponse(
    Guid ProductionRunId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    RuntimeProductionUnitIdentityResponse ProductionUnitIdentity,
    string? LotId,
    string? CarrierId,
    string ActorId,
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
    string EntryOperationId,
    int CompletedOperationCount,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyCollection<ProductionRunOperationResponse> Operations,
    IReadOnlyCollection<ProductionRunRouteDecisionResponse> RouteDecisions);

public sealed record ProductionRunOperationResponse(
    string OperationRunId,
    string OperationId,
    int Attempt,
    string StationSystemId,
    string RuntimeStationId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string ExecutionStatus,
    string Judgement,
    bool IsTerminal,
    Guid? RuntimeSessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyCollection<ProductionRunResourceResponse> Resources,
    IReadOnlyCollection<ProductionRunOutputResponse> Outputs);

public sealed record ProductionRunResourceResponse(
    string Kind,
    string ResourceId,
    long? FencingToken);

public sealed record ProductionRunOutputResponse(
    string Key,
    string Kind,
    string CanonicalValue);

public sealed record ProductionRunRouteDecisionResponse(
    string SourceOperationRunId,
    string TransitionId,
    string TargetOperationId,
    string SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

public sealed record ActiveProductionRunsResponse(
    IReadOnlyCollection<ProductionRunResponse> Runs);

public sealed record ProductionLineRuntimeStateResponse(
    string ProductionLineDefinitionId,
    DateTimeOffset GeneratedAtUtc,
    int ActiveRunCount,
    IReadOnlyCollection<ProductionRunResponse> ActiveRuns);
