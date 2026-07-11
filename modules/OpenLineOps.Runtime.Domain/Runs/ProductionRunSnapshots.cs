using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed record OperationRunSnapshot(
    OperationRunDefinition Definition,
    string OperationRunId,
    int Attempt,
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    RuntimeSessionId? RuntimeSessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyDictionary<string, ProductionContextValue> Outputs,
    IReadOnlyDictionary<ResourceRequirement, long> FencingTokens);

public sealed record RouteDecisionSnapshot(
    string SourceOperationRunId,
    string TransitionId,
    string TargetOperationId,
    ResultJudgement SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

public sealed record ProductionRunSnapshot(
    ProductionRunId RunId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    ProductionUnitIdentity ProductionUnitIdentity,
    string? LotId,
    string? CarrierId,
    string ActorId,
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    ProductDisposition Disposition,
    ProductionRunControlState ControlState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    string EntryOperationId,
    IReadOnlyList<OperationRunDefinition> OperationDefinitions,
    IReadOnlyList<RouteTransitionDefinition> RouteTransitions,
    IReadOnlyList<OperationRunSnapshot> Operations,
    IReadOnlyList<RouteDecisionSnapshot> RouteDecisions,
    IReadOnlyDictionary<string, int> TransitionTraversals);
