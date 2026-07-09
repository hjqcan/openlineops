using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeProcess(
    ProcessDefinitionId ProcessDefinitionId,
    ProcessVersionId ProcessVersionId,
    IReadOnlyList<ExecutableRuntimeNode> Nodes)
{
    public RuntimeNodeId? StartNodeId { get; init; }

    public IReadOnlyList<ExecutableRuntimeRoutingNode> RoutingNodes { get; init; } =
        Array.Empty<ExecutableRuntimeRoutingNode>();

    public IReadOnlyList<ExecutableRuntimeTransition> Transitions { get; init; } =
        Array.Empty<ExecutableRuntimeTransition>();

    public bool UsesGraph =>
        StartNodeId is not null
        || RoutingNodes.Count > 0
        || Transitions.Count > 0;
}
