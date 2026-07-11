using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Topologies;

namespace OpenLineOps.Topology.Application.ProjectWorkspaces;

public interface IProjectAutomationTopologyService
{
    Task<Result<AutomationTopologyDetails>> CreateAsync(
        string projectId,
        string applicationId,
        CreateAutomationTopologyRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> GetByIdAsync(
        string projectId,
        string applicationId,
        string topologyId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<AutomationTopologySummary>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddAutomationSystemRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> UpdateSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string systemId,
        UpdateAutomationSystemRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<TopologyTargetDeletionDetails>> DeleteSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string systemId,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddCapabilityAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddCapabilityContractRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddDriverBindingAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddDriverBindingRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> UpdateDriverBindingAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string bindingId,
        UpdateDriverBindingRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> DeleteDriverBindingAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string bindingId,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddSlotGroupRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> UpdateSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotGroupId,
        UpdateSlotGroupRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<TopologyTargetDeletionDetails>> DeleteSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotGroupId,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> AddSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddSlotDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationTopologyDetails>> UpdateSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotId,
        UpdateSlotDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<TopologyTargetDeletionDetails>> DeleteSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotId,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> CreateLayoutAsync(
        string projectId,
        string applicationId,
        CreateSiteLayoutRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> GetLayoutByIdAsync(
        string projectId,
        string applicationId,
        string layoutId,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> AddLayoutElementAsync(
        string projectId,
        string applicationId,
        string layoutId,
        AddSiteLayoutElementRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> UpdateLayoutElementGeometryAsync(
        string projectId,
        string applicationId,
        string layoutId,
        string elementId,
        UpdateSiteLayoutElementGeometryRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SiteLayoutDetails>> UpdateLayoutElementPresentationAsync(
        string projectId,
        string applicationId,
        string layoutId,
        string elementId,
        UpdateSiteLayoutElementPresentationRequest request,
        CancellationToken cancellationToken = default);
}
