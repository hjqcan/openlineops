using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Application.Validation;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Application.ProjectWorkspaces;

public sealed class ProjectProcessDefinitionService : IProjectProcessDefinitionService
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectProcessDefinitionRepository _repository;
    private readonly IClock _clock;
    private readonly IProcessScriptDefinitionValidator _scriptValidator;

    public ProjectProcessDefinitionService(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectProcessDefinitionRepository repository,
        IClock clock,
        IProcessScriptDefinitionValidator scriptValidator)
    {
        _scopeResolver = scopeResolver;
        _repository = repository;
        _clock = clock;
        _scriptValidator = scriptValidator;
    }

    public Task<Result<ProcessDefinitionDetails>> CreateAsync(
        string projectId,
        string applicationId,
        CreateProcessDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.CreateAsync(request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<ProcessDefinitionDetails>> ReplaceDraftAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CreateProcessDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.ReplaceDraftAsync(processDefinitionId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<ProcessDefinitionDetails>> GetByIdAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.GetByIdAsync(processDefinitionId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<IReadOnlyCollection<ProcessDefinitionSummary>>> ListAsync(
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

    public Task<Result<ProcessGraphValidationReportDetails>> ValidateAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.ValidateAsync(processDefinitionId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<ProcessDefinitionDetails>> PublishAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.PublishAsync(processDefinitionId, cancellationToken),
            cancellationToken);
    }

    private async Task<Result<T>> InScopeAsync<T>(
        string projectId,
        string applicationId,
        Func<ProcessDefinitionAuthoringEngine, Task<Result<T>>> execute,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(applicationId))
        {
            return Result.Failure<T>(ApplicationError.Validation(
                "Processes.ProjectApplicationScopeRequired",
                "ProjectId and ApplicationId are required."));
        }

        var scope = await _scopeResolver
            .ResolveAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return Result.Failure<T>(ApplicationError.NotFound(
                "Processes.ProjectApplicationNotFound",
                $"Application {applicationId} was not found in project {projectId}."));
        }

        var service = new ProcessDefinitionAuthoringEngine(
            new ScopedProcessDefinitionRepository(scope, _repository),
            _clock,
            _scriptValidator);

        return await execute(service).ConfigureAwait(false);
    }

    private sealed class ScopedProcessDefinitionRepository : IProcessDefinitionRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectProcessDefinitionRepository _repository;

        public ScopedProcessDefinitionRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectProcessDefinitionRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public ValueTask SaveAsync(
            ProcessDefinition definition,
            CancellationToken cancellationToken = default)
        {
            return _repository.SaveAsync(_scope, definition, cancellationToken);
        }

        public ValueTask<ProcessDefinition?> GetByIdAsync(
            ProcessDefinitionId definitionId,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetByIdAsync(_scope, definitionId, cancellationToken);
        }

        public ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return _repository.ListAsync(_scope, cancellationToken);
        }
    }
}
