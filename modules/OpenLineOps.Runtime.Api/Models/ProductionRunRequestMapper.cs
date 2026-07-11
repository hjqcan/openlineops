using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Runtime.Api.Models;

internal static class ProductionRunRequestMapper
{
    public static SubmitProductionRunRequest ToApplication(CreateProductionRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProductionUnitIdentity);
        ArgumentNullException.ThrowIfNull(request.Operations);
        ArgumentNullException.ThrowIfNull(request.RouteTransitions);
        return new SubmitProductionRunRequest(
            new ProductionRunId(request.ProductionRunId),
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId,
            request.ProductionLineDefinitionId,
            new ProductionUnitIdentity(
                request.ProductionUnitIdentity.ModelId,
                request.ProductionUnitIdentity.InputKey,
                request.ProductionUnitIdentity.Value),
            request.ActorId,
            request.EntryOperationId,
            request.Operations.Select(ToOperationPlan).ToArray(),
            request.RouteTransitions.Select(transition => new RouteTransitionDefinition(
                transition.TransitionId,
                transition.SourceOperationId,
                transition.TargetOperationId,
                ExactEnum<RuntimeRouteTransitionKind>(transition.Kind, nameof(transition.Kind)),
                transition.RequiredJudgement is null
                    ? null
                    : ExactEnum<ResultJudgement>(
                        transition.RequiredJudgement,
                        nameof(transition.RequiredJudgement)),
                transition.MaxTraversals,
                transition.ParallelGroupId,
                transition.OutputKey is null
                    && transition.ExpectedOutputKind is null
                    && transition.ExpectedOutputValue is null
                        ? null
                        : new RouteOutputCondition(
                            Required(transition.OutputKey, nameof(transition.OutputKey)),
                            new ProductionContextValue(
                                ExactEnum<ProductionContextValueKind>(
                                    Required(
                                        transition.ExpectedOutputKind,
                                        nameof(transition.ExpectedOutputKind)),
                                    nameof(transition.ExpectedOutputKind)),
                                Required(
                                    transition.ExpectedOutputValue,
                                    nameof(transition.ExpectedOutputValue)))))).ToArray(),
            request.LotId,
            request.CarrierId);
    }

    private static OperationExecutionPlan ToOperationPlan(ProductionOperationPlanRequest operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(operation.Resources);
        ArgumentNullException.ThrowIfNull(operation.Process);
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId(operation.ProcessDefinitionId),
            new ProcessVersionId(operation.ProcessVersionId),
            operation.Process.Nodes.Select(ToNode).ToArray())
        {
            StartNodeId = operation.Process.StartNodeId is null
                ? null
                : new RuntimeNodeId(operation.Process.StartNodeId),
            RoutingNodes = operation.Process.RoutingNodes.Select(node =>
                new ExecutableRuntimeRoutingNode(
                    new RuntimeNodeId(node.NodeId),
                    node.DisplayName,
                    ExactEnum<ExecutableRuntimeRoutingNodeKind>(node.Kind, nameof(node.Kind))))
                .ToArray(),
            Transitions = operation.Process.Transitions.Select(transition =>
                new ExecutableRuntimeTransition(
                    new RuntimeNodeId(transition.FromNodeId),
                    new RuntimeNodeId(transition.ToNodeId),
                    transition.Label,
                    transition.MaxTraversals)).ToArray()
        };
        return new OperationExecutionPlan(
            operation.OperationId,
            operation.StationSystemId,
            new StationId(operation.RuntimeStationId),
            new ConfigurationSnapshotId(operation.ConfigurationSnapshotId),
            new RecipeSnapshotId(operation.RecipeSnapshotId),
            process,
            operation.Resources.Select(resource => new ResourceRequirement(
                ExactEnum<ResourceKind>(resource.Kind, nameof(resource.Kind)),
                resource.ResourceId)));
    }

    private static ExecutableRuntimeNode ToNode(RuntimeProcessNodeRequest node)
    {
        if (!double.IsFinite(node.TimeoutMilliseconds) || node.TimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(node),
                "Runtime process node timeout must be finite and positive.");
        }

        return new ExecutableRuntimeNode(
            new RuntimeNodeId(node.NodeId),
            node.DisplayName,
            new RuntimeCapabilityId(node.TargetCapability),
            node.CommandName,
            TimeSpan.FromMilliseconds(node.TimeoutMilliseconds),
            node.InputPayload,
            new RuntimeActionId(node.ActionId),
            new RuntimeTargetReference(node.TargetKind, node.TargetId));
    }

    private static TEnum ExactEnum<TEnum>(string value, string parameterName)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"{parameterName} value '{value}' is not an exact {typeof(TEnum).Name} token.",
            parameterName);
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}
