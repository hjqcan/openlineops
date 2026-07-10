namespace OpenLineOps.Topology.Api.Models;

public sealed record CreateAutomationTopologyRequest(
    string? TopologyId,
    string? DisplayName);

public sealed record AddEquipmentNodeRequest(
    string? NodeId,
    string? ParentNodeId,
    string? Kind,
    string? DisplayName);

public sealed record AddCapabilityContractRequest(
    string? CapabilityId,
    string? CommandName,
    string? Version,
    string? InputSchema,
    string? OutputSchema,
    int? TimeoutSeconds,
    string? SafetyClass);

public sealed record AddAutomationModuleRequest(
    string? ModuleId,
    string? NodeId,
    string? ModuleKind,
    string? DisplayName,
    IReadOnlyCollection<string>? RequiredCapabilityIds,
    IReadOnlyCollection<string>? ProvidedCapabilityIds);

public sealed record AddDriverBindingRequest(
    string? BindingId,
    string? CapabilityId,
    string? ProviderKind,
    string? ProviderKey);

public sealed record AddSlotGroupRequest(
    string? SlotGroupId,
    string? ParentNodeId,
    string? DisplayName,
    string? Kind,
    int? Capacity);

public sealed record AddSlotDefinitionRequest(
    string? SlotGroupId,
    string? SlotId,
    string? ParentNodeId,
    string? Address,
    string? DisplayName,
    string? MaterialKind,
    bool IsEnabled = true);

public sealed record CreateSiteLayoutRequest(
    string? LayoutId,
    string? TopologyId,
    string? DisplayName,
    double? CanvasWidth,
    double? CanvasHeight,
    string? Units);

public sealed record AddSiteLayoutElementRequest(
    string? ElementId,
    string? Kind,
    string? TargetKind,
    string? TargetId,
    double? X,
    double? Y,
    double? Width,
    double? Height,
    double? RotationDegrees,
    string? LayerId,
    string? Label);

public sealed record UpdateSiteLayoutElementGeometryRequest(
    double? X,
    double? Y,
    double? Width,
    double? Height,
    double? RotationDegrees);

public sealed record AutomationTopologyResponse(
    string TopologyId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<EquipmentNodeResponse> Nodes,
    IReadOnlyCollection<AutomationModuleResponse> Modules,
    IReadOnlyCollection<CapabilityContractResponse> Capabilities,
    IReadOnlyCollection<DriverBindingResponse> DriverBindings,
    IReadOnlyCollection<SlotGroupResponse> SlotGroups,
    IReadOnlyCollection<SlotDefinitionResponse> Slots);

public sealed record AutomationTopologySummaryResponse(
    string TopologyId,
    string DisplayName,
    int NodeCount,
    int ModuleCount,
    int SlotCount);

public sealed record EquipmentNodeResponse(
    string NodeId,
    string? ParentNodeId,
    string Kind,
    string DisplayName);

public sealed record AutomationModuleResponse(
    string ModuleId,
    string NodeId,
    string ModuleKind,
    string DisplayName,
    IReadOnlyCollection<string> RequiredCapabilityIds,
    IReadOnlyCollection<string> ProvidedCapabilityIds);

public sealed record CapabilityContractResponse(
    string CapabilityId,
    string CommandName,
    string Version,
    string? InputSchema,
    string? OutputSchema,
    int TimeoutSeconds,
    string SafetyClass);

public sealed record DriverBindingResponse(
    string BindingId,
    string CapabilityId,
    string ProviderKind,
    string ProviderKey);

public sealed record SlotGroupResponse(
    string SlotGroupId,
    string ParentNodeId,
    string DisplayName,
    string Kind,
    int Capacity,
    IReadOnlyCollection<string> SlotIds);

public sealed record SlotDefinitionResponse(
    string SlotId,
    string ParentNodeId,
    string Address,
    string DisplayName,
    string MaterialKind,
    bool IsEnabled);

public sealed record SiteLayoutResponse(
    string LayoutId,
    string TopologyId,
    string DisplayName,
    double CanvasWidth,
    double CanvasHeight,
    string Units,
    IReadOnlyCollection<SiteLayoutElementResponse> Elements);

public sealed record SiteLayoutElementResponse(
    string ElementId,
    string Kind,
    string TargetKind,
    string TargetId,
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    string LayerId,
    string Label);
