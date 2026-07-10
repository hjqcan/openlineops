using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeNode(
    RuntimeNodeId NodeId,
    string DisplayName,
    RuntimeCapabilityId TargetCapability,
    string CommandName,
    TimeSpan Timeout,
    string? InputPayload,
    RuntimeActionId ActionId,
    RuntimeTargetReference Target)
{
    public bool IsValid => Timeout > TimeSpan.Zero
        && ActionId is not null
        && Target is not null
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(CommandName);
}
