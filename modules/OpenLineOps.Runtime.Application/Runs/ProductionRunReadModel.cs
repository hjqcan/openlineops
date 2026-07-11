using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public sealed record ProductionRunReadModel(
    Guid ProductionRunId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    Guid ProductionUnitId,
    ProductionRunUnitIdentityReadModel ProductionUnitIdentity,
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
    IReadOnlyCollection<ProductionRunOperationReadModel> Operations,
    IReadOnlyCollection<ProductionRunRouteDecisionReadModel> RouteDecisions,
    IReadOnlyCollection<ProductionRecoveryDecisionReadModel> RecoveryDecisions);

public sealed record ProductionRunUnitIdentityReadModel(
    string ModelId,
    string InputKey,
    string Value);

public sealed record ProductionRunOperationReadModel(
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
    IReadOnlyCollection<ProductionRunResourceReadModel> Resources,
    IReadOnlyCollection<ProductionRunOutputReadModel> Outputs);

public sealed record ProductionRunResourceReadModel(
    string Kind,
    string ResourceId,
    long? FencingToken);

public sealed record ProductionRunOutputReadModel(
    string Key,
    string Kind,
    string CanonicalValue);

public sealed record ProductionRunRouteDecisionReadModel(
    string SourceOperationRunId,
    string TransitionId,
    string TargetOperationId,
    string SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

public sealed record ProductionRecoveryDecisionReadModel(
    Guid DecisionId,
    string Kind,
    string ActorId,
    string Reason,
    string EvidenceReference,
    DateTimeOffset DecidedAtUtc,
    string? OperationRunId,
    string? OperationId,
    string? ObservedJudgement,
    IReadOnlyCollection<ProductionRunOutputReadModel> ObservedOutputs);

public static class ProductionRunReadModelMapper
{
    public static ProductionRunReadModel ToReadModel(ProductionRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var operations = run.Operations.Select(operation => new ProductionRunOperationReadModel(
            operation.OperationRunId,
            operation.Definition.OperationId,
            operation.Attempt,
            operation.Definition.StationSystemId,
            operation.Definition.StationId.Value,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            operation.ExecutionStatus.ToString(),
            operation.Judgement.ToString(),
            IsTerminal(operation.ExecutionStatus),
            operation.RuntimeSessionId?.Value,
            operation.StartedAtUtc,
            operation.CompletedAtUtc,
            operation.FailureCode,
            operation.FailureReason,
            operation.CompletedStepCount,
            operation.CommandCount,
            operation.IncidentCount,
            operation.Definition.ResourceRequirements
                .Concat(operation.FencingTokens.Keys)
                .Distinct()
                .Select(resource => new ProductionRunResourceReadModel(
                    resource.Kind.ToString(),
                    resource.ResourceId,
                    operation.FencingTokens.GetValueOrDefault(resource)))
                .ToArray(),
            operation.Outputs.OrderBy(output => output.Key, StringComparer.Ordinal)
                .Select(output => new ProductionRunOutputReadModel(
                output.Key,
                output.Value.Kind.ToString(),
                output.Value.CanonicalValue)).ToArray())).ToArray();

        return new ProductionRunReadModel(
            run.RunId.Value,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId,
            run.ProductionLineDefinitionId,
            run.ProductionUnitId.Value,
            new ProductionRunUnitIdentityReadModel(
                run.ProductionUnitIdentity.ModelId,
                run.ProductionUnitIdentity.InputKey,
                run.ProductionUnitIdentity.Value),
            run.LotId,
            run.CarrierId,
            run.ActorId,
            run.ExecutionStatus.ToString(),
            run.Judgement.ToString(),
            run.Disposition.ToString(),
            run.ControlState.ToString(),
            IsTerminal(run.ExecutionStatus),
            run.CreatedAtUtc,
            run.LastTransitionAtUtc,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.FailureCode,
            run.FailureReason,
            run.EntryOperationId,
            operations.Count(operation => operation.ExecutionStatus == ExecutionStatus.Completed.ToString()),
            operations.Sum(static operation => operation.CompletedStepCount),
            operations.Sum(static operation => operation.CommandCount),
            operations.Sum(static operation => operation.IncidentCount),
            operations,
            run.RouteDecisions.Select(decision => new ProductionRunRouteDecisionReadModel(
                decision.SourceOperationRunId,
                decision.TransitionId,
                decision.TargetOperationId,
                decision.SourceJudgement.ToString(),
                decision.Traversal,
                decision.DecidedAtUtc)).ToArray(),
            run.RecoveryDecisions
                .OrderBy(decision => decision.DecidedAtUtc)
                .ThenBy(decision => decision.DecisionId)
                .Select(decision => new ProductionRecoveryDecisionReadModel(
                decision.DecisionId,
                decision.Kind.ToString(),
                decision.ActorId,
                decision.Reason,
                decision.EvidenceReference,
                decision.DecidedAtUtc,
                decision.OperationRunId,
                decision.OperationId,
                decision.ObservedJudgement?.ToString(),
                decision.ObservedOutputs.OrderBy(output => output.Key, StringComparer.Ordinal)
                    .Select(output => new ProductionRunOutputReadModel(
                    output.Key,
                    output.Value.Kind.ToString(),
                    output.Value.CanonicalValue)).ToArray())).ToArray());
    }

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;
}
