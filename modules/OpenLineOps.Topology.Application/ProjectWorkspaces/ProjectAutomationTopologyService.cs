using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.Topologies;

namespace OpenLineOps.Topology.Application.ProjectWorkspaces;

public sealed class ProjectAutomationTopologyService : IProjectAutomationTopologyService
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectAutomationTopologyRepository _topologyRepository;
    private readonly IProjectSiteLayoutRepository _layoutRepository;
    private readonly IClock _clock;

    public ProjectAutomationTopologyService(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectAutomationTopologyRepository topologyRepository,
        IProjectSiteLayoutRepository layoutRepository,
        IClock clock)
    {
        _scopeResolver = scopeResolver;
        _topologyRepository = topologyRepository;
        _layoutRepository = layoutRepository;
        _clock = clock;
    }

    public Task<Result<AutomationTopologyDetails>> CreateAsync(
        string projectId,
        string applicationId,
        CreateAutomationTopologyRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.CreateAsync(request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> GetByIdAsync(
        string projectId,
        string applicationId,
        string topologyId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.GetByIdAsync(topologyId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<IReadOnlyCollection<AutomationTopologySummary>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.ListAsync(cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> AddSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddAutomationSystemRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.AddSystemAsync(topologyId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> UpdateSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string systemId,
        UpdateAutomationSystemRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.UpdateSystemAsync(topologyId, systemId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<TopologyTargetDeletionDetails>> DeleteSystemAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string systemId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.DeleteSystemAsync(topologyId, systemId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> AddCapabilityAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddCapabilityContractRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.AddCapabilityAsync(topologyId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> AddDriverBindingAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddDriverBindingRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.AddDriverBindingAsync(topologyId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> AddSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddSlotGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.AddSlotGroupAsync(topologyId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> UpdateSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotGroupId,
        UpdateSlotGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.UpdateSlotGroupAsync(topologyId, slotGroupId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<TopologyTargetDeletionDetails>> DeleteSlotGroupAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotGroupId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.DeleteSlotGroupAsync(topologyId, slotGroupId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> AddSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddSlotDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.AddSlotAsync(topologyId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<AutomationTopologyDetails>> UpdateSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotId,
        UpdateSlotDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.UpdateSlotAsync(topologyId, slotId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<TopologyTargetDeletionDetails>> DeleteSlotAsync(
        string projectId,
        string applicationId,
        string topologyId,
        string slotId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.DeleteSlotAsync(topologyId, slotId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<SiteLayoutDetails>> CreateLayoutAsync(
        string projectId,
        string applicationId,
        CreateSiteLayoutRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.CreateLayoutAsync(request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<SiteLayoutDetails>> GetLayoutByIdAsync(
        string projectId,
        string applicationId,
        string layoutId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.GetLayoutByIdAsync(layoutId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<SiteLayoutDetails>> AddLayoutElementAsync(
        string projectId,
        string applicationId,
        string layoutId,
        AddSiteLayoutElementRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.AddLayoutElementAsync(layoutId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<SiteLayoutDetails>> UpdateLayoutElementGeometryAsync(
        string projectId,
        string applicationId,
        string layoutId,
        string elementId,
        UpdateSiteLayoutElementGeometryRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.UpdateLayoutElementGeometryAsync(layoutId, elementId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<SiteLayoutDetails>> UpdateLayoutElementPresentationAsync(
        string projectId,
        string applicationId,
        string layoutId,
        string elementId,
        UpdateSiteLayoutElementPresentationRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.UpdateLayoutElementPresentationAsync(layoutId, elementId, request, cancellationToken),
            cancellationToken);
    }

    private async Task<Result<T>> InScopeAsync<T>(
        string projectId,
        string applicationId,
        Func<ApplicationAutomationTopologyEditor, Task<Result<T>>> execute,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(applicationId))
        {
            return Result.Failure<T>(ApplicationError.Validation(
                "Topology.ProjectApplicationScopeRequired",
                "ProjectId and ApplicationId are required."));
        }

        var scope = await _scopeResolver
            .ResolveAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return Result.Failure<T>(ApplicationError.NotFound(
                "Topology.ProjectApplicationNotFound",
                $"Application {applicationId} was not found in project {projectId}."));
        }

        var service = new ApplicationAutomationTopologyEditor(
            scope,
            _topologyRepository,
            _layoutRepository,
            _clock);

        return await execute(service).ConfigureAwait(false);
    }

}
