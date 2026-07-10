using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Topology.Application.Layouts;

namespace OpenLineOps.Topology.Application.Topologies;

public interface IAutomationTopologyService
{
    Task<Result<AutomationTopologyDetails>> CreateAsync(
        CreateAutomationTopologyRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> GetByIdAsync(
        string topologyId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<AutomationTopologySummary>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddEquipmentNodeAsync(
        string topologyId,
        AddEquipmentNodeRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddCapabilityAsync(
        string topologyId,
        AddCapabilityContractRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddModuleAsync(
        string topologyId,
        AddAutomationModuleRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddDriverBindingAsync(
        string topologyId,
        AddDriverBindingRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddSlotGroupAsync(
        string topologyId,
        AddSlotGroupRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddSlotAsync(
        string topologyId,
        AddSlotDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> CreateLayoutAsync(
        CreateSiteLayoutRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> GetLayoutByIdAsync(
        string layoutId,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> AddLayoutElementAsync(
        string layoutId,
        AddSiteLayoutElementRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> UpdateLayoutElementGeometryAsync(
        string layoutId,
        string elementId,
        UpdateSiteLayoutElementGeometryRequest request,
        CancellationToken cancellationToken = default);
}
