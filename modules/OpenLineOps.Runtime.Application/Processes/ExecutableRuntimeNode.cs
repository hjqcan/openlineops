using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeNode(
    RuntimeNodeId NodeId,
    string DisplayName,
    RuntimeCapabilityId TargetCapability,
    string CommandName,
    TimeSpan Timeout,
    string? InputPayload = null,
    RuntimeActionId? ActionId = null,
    ExecutableRuntimeDynamicActionSlot? DynamicChildren = null)
{
    public RuntimeActionId EffectiveActionId =>
        ActionId ?? new RuntimeActionId($"{NodeId.Value}:action:1");

    public bool IsValid => Timeout > TimeSpan.Zero
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(CommandName)
        && (DynamicChildren is null || DynamicChildren.IsValid);
}
