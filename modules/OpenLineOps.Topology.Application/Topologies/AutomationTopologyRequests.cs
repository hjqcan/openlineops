namespace OpenLineOps.Topology.Application.Topologies;

public sealed record CreateAutomationTopologyRequest(
    string TopologyId,
    string DisplayName);

public sealed record AddAutomationSystemRequest(
    string SystemId,
    string? ParentSystemId,
    string Kind,
    string SystemType,
    string DisplayName,
    IReadOnlyCollection<string> RequiredCapabilityIds,
    IReadOnlyCollection<string> ProvidedCapabilityIds,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record UpdateAutomationSystemRequest(
    string? SystemType,
    string? DisplayName,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record AddCapabilityContractRequest(
    string CapabilityId,
    string CommandName,
    string Version,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutSeconds,
    string SafetyClass);

public sealed record AddDriverBindingRequest(
    string BindingId,
    string CapabilityId,
    string ProviderKind,
    string ProviderKey);

public sealed record AddSlotGroupRequest(
    string SlotGroupId,
    string ParentSystemId,
    string DisplayName,
    string Kind,
    int Capacity);

public sealed record UpdateSlotGroupRequest(
    string? DisplayName,
    string? Kind,
    int? Capacity);

public sealed record AddSlotDefinitionRequest(
    string SlotGroupId,
    string SlotId,
    string ParentSystemId,
    string Address,
    string DisplayName,
    string MaterialKind,
    bool IsEnabled = true);

public sealed record UpdateSlotDefinitionRequest(
    string? Address,
    string? DisplayName,
    string? MaterialKind,
    bool? IsEnabled);

public sealed record TopologyTargetDeletionDetails(
    AutomationTopologyDetails Topology,
    int UpdatedLayoutCount,
    int RemovedLayoutElementCount,
    string PublicationImpact);
