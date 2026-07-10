using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Application.Layouts;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Application.Topologies;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;
using OpenLineOps.Topology.Domain.Topology;

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

    public Task<Result<AutomationTopologyDetails>> AddEquipmentNodeAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddEquipmentNodeRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.AddEquipmentNodeAsync(topologyId, request, cancellationToken),
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

    public Task<Result<AutomationTopologyDetails>> AddModuleAsync(
        string projectId,
        string applicationId,
        string topologyId,
        AddAutomationModuleRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.AddModuleAsync(topologyId, request, cancellationToken),
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

    private async Task<Result<T>> InScopeAsync<T>(
        string projectId,
        string applicationId,
        Func<IAutomationTopologyService, Task<Result<T>>> execute,
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

        var service = new AutomationTopologyService(
            new ScopedAutomationTopologyRepository(scope, _topologyRepository),
            new ScopedSiteLayoutRepository(scope, _layoutRepository),
            _clock);

        return await execute(service).ConfigureAwait(false);
    }

    private sealed class ScopedAutomationTopologyRepository : IAutomationTopologyRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectAutomationTopologyRepository _repository;

        public ScopedAutomationTopologyRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectAutomationTopologyRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public ValueTask SaveAsync(AutomationTopology topology, CancellationToken cancellationToken = default)
        {
            return _repository.SaveAsync(_scope, topology, cancellationToken);
        }

        public ValueTask<AutomationTopology?> GetByIdAsync(
            AutomationTopologyId topologyId,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetByIdAsync(_scope, topologyId, cancellationToken);
        }

        public ValueTask<IReadOnlyCollection<AutomationTopology>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return _repository.ListAsync(_scope, cancellationToken);
        }
    }

    private sealed class ScopedSiteLayoutRepository : ISiteLayoutRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectSiteLayoutRepository _repository;

        public ScopedSiteLayoutRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectSiteLayoutRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public ValueTask SaveAsync(SiteLayout layout, CancellationToken cancellationToken = default)
        {
            return _repository.SaveAsync(_scope, layout, cancellationToken);
        }

        public ValueTask<SiteLayout?> GetByIdAsync(
            SiteLayoutId layoutId,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetByIdAsync(_scope, layoutId, cancellationToken);
        }

        public ValueTask<IReadOnlyCollection<SiteLayout>> ListByTopologyAsync(
            AutomationTopologyId topologyId,
            CancellationToken cancellationToken = default)
        {
            return _repository.ListByTopologyAsync(_scope, topologyId, cancellationToken);
        }
    }
}
