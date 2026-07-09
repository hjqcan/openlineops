using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeNode(
    RuntimeNodeId NodeId,
    string DisplayName,
    RuntimeCapabilityId TargetCapability,
    string CommandName,
    TimeSpan Timeout,
    string? InputPayload = null)
{
    public bool IsValid => Timeout > TimeSpan.Zero
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(CommandName);
}
