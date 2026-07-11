namespace OpenLineOps.Topology.Application.Topologies;

public sealed record AutomationTopologyDetails(
    string TopologyId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<AutomationSystemDetails> Systems,
    IReadOnlyCollection<CapabilityContractDetails> Capabilities,
    IReadOnlyCollection<DriverBindingDetails> DriverBindings,
    IReadOnlyCollection<SlotGroupDetails> SlotGroups,
    IReadOnlyCollection<SlotDefinitionDetails> Slots);

public sealed record AutomationTopologySummary(
    string TopologyId,
    string DisplayName,
    int SystemCount,
    int StationCount,
    int SlotCount);

public sealed record AutomationSystemDetails(
    string SystemId,
    string? ParentSystemId,
    string Kind,
    string SystemType,
    string DisplayName,
    IReadOnlyCollection<string> RequiredCapabilityIds,
    IReadOnlyCollection<string> ProvidedCapabilityIds,
    IReadOnlyDictionary<string, string> Metadata);

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
    string OwnerSystemId,
    string CapabilityId,
    string ProviderKind,
    string ProviderKey);

public sealed record SlotGroupDetails(
    string SlotGroupId,
    string ParentSystemId,
    string DisplayName,
    string Kind,
    int Capacity,
    IReadOnlyCollection<string> SlotIds);

public sealed record SlotDefinitionDetails(
    string SlotId,
    string SlotGroupId,
    string ParentSystemId,
    string Address,
    string DisplayName,
    string MaterialKind,
    bool IsEnabled);
