using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Runtime.Application.Sessions;

namespace OpenLineOps.Processes.Application.Runtime;

public sealed class ProjectProcessRuntimeSessionLauncher : IProjectProcessRuntimeSessionLauncher
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectProcessDefinitionRepository _projectRepository;
    private readonly IProcessRuntimeSessionLauncher _legacyLauncher;
    private readonly IRuntimeSessionRunner _sessionRunner;
    private readonly IRuntimeConfigurationSnapshotResolver _configurationSnapshotResolver;

    public ProjectProcessRuntimeSessionLauncher(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectProcessDefinitionRepository projectRepository,
        IProcessRuntimeSessionLauncher legacyLauncher,
        IRuntimeSessionRunner sessionRunner,
        IRuntimeConfigurationSnapshotResolver configurationSnapshotResolver)
    {
        _scopeResolver = scopeResolver;
        _projectRepository = projectRepository;
        _legacyLauncher = legacyLauncher;
        _sessionRunner = sessionRunner;
        _configurationSnapshotResolver = configurationSnapshotResolver;
    }

    public async ValueTask<Result<StartedProcessRuntimeSessionDetails>> StartAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        StartProcessRuntimeSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = await _scopeResolver
            .ResolveAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return Result.Failure<StartedProcessRuntimeSessionDetails>(ApplicationError.NotFound(
                "Processes.ProjectApplicationNotFound",
                $"Application {applicationId} was not found in project {projectId}."));
        }

        var definitionId = new ProcessDefinitionId(processDefinitionId);
        var projectDefinition = await _projectRepository
            .GetByIdAsync(scope, definitionId, cancellationToken)
            .ConfigureAwait(false);

        if (projectDefinition is null)
        {
            // Compatibility for manifests created before project-scoped process
            // source existed. New Studio projects always take the scoped path.
            return await _legacyLauncher
                .StartAsync(processDefinitionId, request, cancellationToken)
                .ConfigureAwait(false);
        }

        var launcher = new ProcessRuntimeSessionLauncher(
            new ScopedProcessDefinitionRepository(scope, _projectRepository),
            _sessionRunner,
            _configurationSnapshotResolver);

        return await launcher
            .StartAsync(processDefinitionId, request, cancellationToken)
            .ConfigureAwait(false);
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
