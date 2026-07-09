using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeRoutingNode(
    RuntimeNodeId NodeId,
    string DisplayName,
    ExecutableRuntimeRoutingNodeKind Kind)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(DisplayName);
}
