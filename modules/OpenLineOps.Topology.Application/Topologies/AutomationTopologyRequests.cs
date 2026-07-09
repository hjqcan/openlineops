namespace OpenLineOps.Topology.Application.Topologies;

public sealed record CreateAutomationTopologyRequest(
    string TopologyId,
    string DisplayName);

public sealed record AddEquipmentNodeRequest(
    string NodeId,
    string? ParentNodeId,
    string Kind,
    string DisplayName);

public sealed record AddCapabilityContractRequest(
    string CapabilityId,
    string CommandName,
    string Version,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutSeconds,
    string SafetyClass);

public sealed record AddAutomationModuleRequest(
    string ModuleId,
    string NodeId,
    string ModuleKind,
    string DisplayName,
    IReadOnlyCollection<string> RequiredCapabilityIds,
    IReadOnlyCollection<string> ProvidedCapabilityIds);

public sealed record AddDriverBindingRequest(
    string BindingId,
    string CapabilityId,
    string ProviderKind,
    string ProviderKey);

public sealed record AddSlotGroupRequest(
    string SlotGroupId,
    string ParentNodeId,
    string DisplayName,
    string Kind,
    int Capacity);

public sealed record AddSlotDefinitionRequest(
    string SlotGroupId,
    string SlotId,
    string ParentNodeId,
    string Address,
    string DisplayName,
    string MaterialKind,
    bool IsEnabled = true);
