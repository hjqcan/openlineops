using System.Text.Json.Serialization;

namespace OpenLineOps.Topology.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAutomationTopologyRequest(
    string? TopologyId,
    string? DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddAutomationSystemRequest(
    string? SystemId,
    string? ParentSystemId,
    string? Kind,
    string? SystemType,
    string? DisplayName,
    IReadOnlyCollection<string>? RequiredCapabilityIds,
    IReadOnlyCollection<string>? ProvidedCapabilityIds,
    IReadOnlyDictionary<string, string>? Metadata);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAutomationSystemRequest(
    string? SystemType,
    string? DisplayName,
    IReadOnlyDictionary<string, string>? Metadata);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddCapabilityContractRequest(
    string? CapabilityId,
    string? CommandName,
    string? Version,
    string? InputSchema,
    string? OutputSchema,
    int? TimeoutSeconds,
    string? SafetyClass);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddDriverBindingRequest(
    string? BindingId,
    string? OwnerSystemId,
    string? CapabilityId,
    string? ProviderKind,
    string? ProviderKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateDriverBindingRequest(
    string? OwnerSystemId,
    string? CapabilityId,
    string? ProviderKind,
    string? ProviderKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddSlotGroupRequest(
    string? SlotGroupId,
    string? ParentSystemId,
    string? DisplayName,
    string? Kind,
    int? Capacity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSlotGroupRequest(
    string? DisplayName,
    string? Kind,
    int? Capacity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddSlotDefinitionRequest(
    string? SlotGroupId,
    string? SlotId,
    string? ParentSystemId,
    string? Address,
    string? DisplayName,
    string? MaterialKind,
    bool IsEnabled = true);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSlotDefinitionRequest(
    string? Address,
    string? DisplayName,
    string? MaterialKind,
    bool? IsEnabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSiteLayoutRequest(
    string? LayoutId,
    string? TopologyId,
    string? DisplayName,
    double? CanvasWidth,
    double? CanvasHeight,
    string? Units);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LayoutTargetRequest(
    string? Kind,
    string? TargetId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddSiteLayoutElementRequest(
    string? ElementId,
    string? Kind,
    LayoutTargetRequest? Target,
    string? ParentElementId,
    double? X,
    double? Y,
    double? Width,
    double? Height,
    double? RotationDegrees,
    int? ZIndex,
    IReadOnlyDictionary<string, string>? Style);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSiteLayoutElementGeometryRequest(
    double? X,
    double? Y,
    double? Width,
    double? Height,
    double? RotationDegrees);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSiteLayoutElementPresentationRequest(
    int? ZIndex,
    IReadOnlyDictionary<string, string>? Style);

public sealed record AutomationTopologyResponse(
    string TopologyId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<AutomationSystemResponse> Systems,
    IReadOnlyCollection<CapabilityContractResponse> Capabilities,
    IReadOnlyCollection<DriverBindingResponse> DriverBindings,
    IReadOnlyCollection<SlotGroupResponse> SlotGroups,
    IReadOnlyCollection<SlotDefinitionResponse> Slots,
    string Revision);

public sealed record TopologyTargetDeletionResponse(
    AutomationTopologyResponse Topology,
    int UpdatedLayoutCount,
    int RemovedLayoutElementCount,
    string PublicationImpact);

public sealed record AutomationTopologySummaryResponse(
    string TopologyId,
    string DisplayName,
    int SystemCount,
    int StationCount,
    int SlotCount);

public sealed record AutomationSystemResponse(
    string SystemId,
    string? ParentSystemId,
    string Kind,
    string SystemType,
    string DisplayName,
    IReadOnlyCollection<string> RequiredCapabilityIds,
    IReadOnlyCollection<string> ProvidedCapabilityIds,
    IReadOnlyDictionary<string, string> Metadata);

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
    string OwnerSystemId,
    string CapabilityId,
    string ProviderKind,
    string ProviderKey);

public sealed record SlotGroupResponse(
    string SlotGroupId,
    string ParentSystemId,
    string DisplayName,
    string Kind,
    int Capacity,
    IReadOnlyCollection<string> SlotIds);

public sealed record SlotDefinitionResponse(
    string SlotId,
    string SlotGroupId,
    string ParentSystemId,
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
    IReadOnlyCollection<SiteLayoutElementResponse> Elements,
    string Revision);

public sealed record LayoutTargetResponse(
    string Kind,
    string TargetId);

public sealed record SiteLayoutElementResponse(
    string ElementId,
    string Kind,
    LayoutTargetResponse Target,
    string? ParentElementId,
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    int ZIndex,
    IReadOnlyDictionary<string, string> Style);
