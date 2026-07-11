using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Api.Models;

internal static class ProductionRunResponseMapper
{
    public static ProductionRunResponse ToResponse(ProductionRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var operations = run.Operations.Select(operation => new ProductionRunOperationResponse(
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
            operation.Definition.ResourceRequirements.Select(resource =>
                new ProductionRunResourceResponse(
                    resource.Kind.ToString(),
                    resource.ResourceId,
                    operation.FencingTokens.GetValueOrDefault(resource))).ToArray(),
            operation.Outputs.Select(output => new ProductionRunOutputResponse(
                output.Key,
                output.Value.Kind.ToString(),
                output.Value.CanonicalValue)).ToArray())).ToArray();

        return new ProductionRunResponse(
            run.RunId.Value,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId,
            run.ProductionLineDefinitionId,
            new RuntimeProductionUnitIdentityResponse(
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
            run.RouteDecisions.Select(decision => new ProductionRunRouteDecisionResponse(
                decision.SourceOperationRunId,
                decision.TransitionId,
                decision.TargetOperationId,
                decision.SourceJudgement.ToString(),
                decision.Traversal,
                decision.DecidedAtUtc)).ToArray());
    }

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;
}
