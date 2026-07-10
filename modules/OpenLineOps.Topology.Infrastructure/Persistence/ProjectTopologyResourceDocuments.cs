namespace OpenLineOps.Topology.Infrastructure.Persistence;

internal sealed record ProjectAutomationTopologyDocument(
    int FormatVersion,
    string ResourceKind,
    string ApplicationId,
    string TopologyId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    EquipmentNodeDocument[] Nodes,
    AutomationModuleDocument[] Modules,
    CapabilityContractDocument[] Capabilities,
    DriverBindingDocument[] DriverBindings,
    SlotGroupDocument[] SlotGroups,
    SlotDefinitionDocument[] Slots)
{
    public const int CurrentFormatVersion = 2;
    public const string Kind = "OpenLineOps.AutomationTopology";
}

internal sealed record EquipmentNodeDocument(
    string NodeId,
    string? ParentNodeId,
    string Kind,
    string DisplayName);

internal sealed record AutomationModuleDocument(
    string ModuleId,
    string NodeId,
    string ModuleKind,
    string DisplayName,
    string[] RequiredCapabilityIds,
    string[] ProvidedCapabilityIds);

internal sealed record CapabilityContractDocument(
    string CapabilityId,
    string CommandName,
    string Version,
    string? InputSchema,
    string? OutputSchema,
    long TimeoutTicks,
    string SafetyClass);

internal sealed record DriverBindingDocument(
    string BindingId,
    string CapabilityId,
    string ProviderKind,
    string ProviderKey);

internal sealed record SlotGroupDocument(
    string SlotGroupId,
    string ParentNodeId,
    string DisplayName,
    string Kind,
    int Capacity);

internal sealed record SlotDefinitionDocument(
    string SlotGroupId,
    string SlotId,
    string ParentNodeId,
    string Address,
    string DisplayName,
    string MaterialKind,
    bool IsEnabled);

internal sealed record ProjectSiteLayoutDocument(
    int FormatVersion,
    string ResourceKind,
    string ApplicationId,
    string LayoutId,
    string TopologyId,
    string DisplayName,
    double CanvasWidth,
    double CanvasHeight,
    string Units,
    SiteLayoutElementDocument[] Elements)
{
    public const int CurrentFormatVersion = 2;
    public const string Kind = "OpenLineOps.SiteLayout";
}

internal sealed record SiteLayoutElementDocument(
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
