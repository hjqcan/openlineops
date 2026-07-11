using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

internal sealed record ProjectAutomationTopologyDocument(
    string SchemaVersion,
    string ResourceKind,
    string ApplicationId,
    string TopologyId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    AutomationSystemDocument[] Systems,
    CapabilityContractDocument[] Capabilities,
    DriverBindingDocument[] DriverBindings,
    SlotGroupDocument[] SlotGroups,
    SlotDefinitionDocument[] Slots)
{
    public const string CurrentSchemaVersion = ApplicationResourceSchemaVersions.AutomationTopology;
    public const string Kind = "OpenLineOps.AutomationTopology";
}

internal sealed record AutomationSystemDocument(
    string SystemId,
    string? ParentSystemId,
    string Kind,
    string SystemType,
    string DisplayName,
    string[] RequiredCapabilityIds,
    string[] ProvidedCapabilityIds,
    IReadOnlyDictionary<string, string> Metadata);

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
    string OwnerSystemId,
    string CapabilityId,
    string ProviderKind,
    string ProviderKey);

internal sealed record SlotGroupDocument(
    string SlotGroupId,
    string ParentSystemId,
    string DisplayName,
    string Kind,
    int Capacity);

internal sealed record SlotDefinitionDocument(
    string SlotId,
    string SlotGroupId,
    string ParentSystemId,
    string Address,
    string DisplayName,
    string MaterialKind,
    bool IsEnabled);

internal sealed record ProjectSiteLayoutDocument(
    string SchemaVersion,
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
    public const string CurrentSchemaVersion = ApplicationResourceSchemaVersions.SiteLayout;
    public const string Kind = "OpenLineOps.SiteLayout";
}

internal sealed record LayoutTargetDocument(
    string Kind,
    string TargetId);

internal sealed record SiteLayoutElementDocument(
    string ElementId,
    string Kind,
    LayoutTargetDocument Target,
    string? ParentElementId,
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    int ZIndex,
    IReadOnlyDictionary<string, string> Style);
