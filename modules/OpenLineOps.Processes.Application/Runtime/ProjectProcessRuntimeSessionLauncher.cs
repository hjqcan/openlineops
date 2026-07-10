using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Runtime.Application.Sessions;

namespace OpenLineOps.Processes.Application.Runtime;

public sealed class ProjectProcessRuntimeSessionLauncher : IProjectProcessRuntimeSessionLauncher
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectProcessDefinitionRepository _projectRepository;
    private readonly IProcessDefinitionRepository _legacyRepository;
    private readonly IRuntimeSessionRunner _sessionRunner;
    private readonly IProjectRuntimeConfigurationSnapshotResolver _projectConfigurationSnapshotResolver;
    private readonly IRuntimeConfigurationSnapshotResolver _legacyConfigurationSnapshotResolver;
    private readonly IProcessFlowIrCompiler _flowIrCompiler;
    private readonly IFlowIrExecutableRuntimeProcessMapper _flowIrMapper;

    public ProjectProcessRuntimeSessionLauncher(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectProcessDefinitionRepository projectRepository,
        IProcessDefinitionRepository legacyRepository,
        IRuntimeSessionRunner sessionRunner,
        IProjectRuntimeConfigurationSnapshotResolver projectConfigurationSnapshotResolver,
        IRuntimeConfigurationSnapshotResolver legacyConfigurationSnapshotResolver,
        IProcessFlowIrCompiler flowIrCompiler,
        IFlowIrExecutableRuntimeProcessMapper flowIrMapper)
    {
        _scopeResolver = scopeResolver;
        _projectRepository = projectRepository;
        _legacyRepository = legacyRepository;
        _sessionRunner = sessionRunner;
        _projectConfigurationSnapshotResolver = projectConfigurationSnapshotResolver;
        _legacyConfigurationSnapshotResolver = legacyConfigurationSnapshotResolver;
        _flowIrCompiler = flowIrCompiler;
        _flowIrMapper = flowIrMapper;
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

        // Process definitions created before project-scoped source existed can
        // still come from the legacy repository. Snapshot resolution remains
        // project-first in both branches so a global definition can safely run
        // with a configuration snapshot owned by this application.
        var definitionRepository = projectDefinition is null
            ? _legacyRepository
            : new ScopedProcessDefinitionRepository(scope, _projectRepository);
        var launcher = new ProcessRuntimeSessionLauncher(
            definitionRepository,
            _sessionRunner,
            new ProjectFirstRuntimeConfigurationSnapshotResolver(
                scope,
                _projectConfigurationSnapshotResolver,
                _legacyConfigurationSnapshotResolver),
            _flowIrCompiler,
            _flowIrMapper);

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

    private sealed class ProjectFirstRuntimeConfigurationSnapshotResolver :
        IRuntimeConfigurationSnapshotResolver
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectRuntimeConfigurationSnapshotResolver _projectResolver;
        private readonly IRuntimeConfigurationSnapshotResolver _legacyResolver;

        public ProjectFirstRuntimeConfigurationSnapshotResolver(
            ProjectApplicationWorkspaceScope scope,
            IProjectRuntimeConfigurationSnapshotResolver projectResolver,
            IRuntimeConfigurationSnapshotResolver legacyResolver)
        {
            _scope = scope;
            _projectResolver = projectResolver;
            _legacyResolver = legacyResolver;
        }

        public async ValueTask<Result<RuntimeConfigurationSnapshotDetails>> ResolveAsync(
            string configurationSnapshotId,
            CancellationToken cancellationToken = default)
        {
            var projectResult = await _projectResolver
                .ResolveAsync(_scope, configurationSnapshotId, cancellationToken)
                .ConfigureAwait(false);
            if (projectResult.IsSuccess
                || !projectResult.Error.Code.StartsWith("NotFound.", StringComparison.Ordinal))
            {
                return projectResult;
            }

            return await _legacyResolver
                .ResolveAsync(configurationSnapshotId, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
