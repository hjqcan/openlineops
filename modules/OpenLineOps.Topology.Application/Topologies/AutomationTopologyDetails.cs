namespace OpenLineOps.Topology.Application.Topologies;

public sealed record AutomationTopologyDetails(
    string TopologyId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<EquipmentNodeDetails> Nodes,
    IReadOnlyCollection<AutomationModuleDetails> Modules,
    IReadOnlyCollection<CapabilityContractDetails> Capabilities,
    IReadOnlyCollection<DriverBindingDetails> DriverBindings,
    IReadOnlyCollection<SlotGroupDetails> SlotGroups,
    IReadOnlyCollection<SlotDefinitionDetails> Slots);

public sealed record AutomationTopologySummary(
    string TopologyId,
    string DisplayName,
    int NodeCount,
    int ModuleCount,
    int SlotCount);

public sealed record EquipmentNodeDetails(
    string NodeId,
    string? ParentNodeId,
    string Kind,
    string DisplayName);

public sealed record AutomationModuleDetails(
    string ModuleId,
    string NodeId,
    string ModuleKind,
    string DisplayName,
    IReadOnlyCollection<string> RequiredCapabilityIds,
    IReadOnlyCollection<string> ProvidedCapabilityIds);

public sealed record CapabilityContractDetails(
    string CapabilityId,
    string CommandName,
    string Version,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutSeconds,
    string SafetyClass);

public sealed record DriverBindingDetails(
    string BindingId,
    string CapabilityId,
    string ProviderKind,
    string ProviderKey);

public sealed record SlotGroupDetails(
    string SlotGroupId,
    string ParentNodeId,
    string DisplayName,
    string Kind,
    int Capacity,
    IReadOnlyCollection<string> SlotIds);

public sealed record SlotDefinitionDetails(
    string SlotId,
    string ParentNodeId,
    string Address,
    string DisplayName,
    string MaterialKind,
    bool IsEnabled);
