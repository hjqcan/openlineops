using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;

namespace OpenLineOps.Processes.Application.ProjectWorkspaces;

public sealed class ProjectProcessBlocklyBlockCatalog : IProjectProcessBlocklyBlockCatalog
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectProcessBlocklyBlockDefinitionRepository _repository;
    private readonly IClock _clock;
    private readonly IProcessBlocklyBlockCatalogSource[] _sources;

    public ProjectProcessBlocklyBlockCatalog(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectProcessBlocklyBlockDefinitionRepository repository,
        IClock clock,
        IEnumerable<IProcessBlocklyBlockCatalogSource>? sources = null)
    {
        _scopeResolver = scopeResolver;
        _repository = repository;
        _clock = clock;
        _sources = sources?.ToArray() ?? [];
    }

    public Task<Result<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            catalog => catalog.ListAsync(cancellationToken),
            cancellationToken);
    }

    public Task<Result<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>> ListVersionsAsync(
        string projectId,
        string applicationId,
        string blockType,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            catalog => catalog.ListVersionsAsync(blockType, cancellationToken),
            cancellationToken);
    }

    public Task<Result<ProcessBlocklyBlockDefinitionDetails>> RegisterAsync(
        string projectId,
        string applicationId,
        RegisterProcessBlocklyBlockDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            catalog => catalog.RegisterAsync(request, cancellationToken),
            cancellationToken);
    }

    private async Task<Result<T>> InScopeAsync<T>(
        string projectId,
        string applicationId,
        Func<IProcessBlocklyBlockCatalog, Task<Result<T>>> execute,
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

        var catalog = new ProcessBlocklyBlockCatalog(
            new ScopedBlockRepository(scope, _repository),
            _clock,
            _sources,
            scope);

        return await execute(catalog).ConfigureAwait(false);
    }

    private sealed class ScopedBlockRepository : IProcessBlocklyBlockDefinitionRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectProcessBlocklyBlockDefinitionRepository _repository;

        public ScopedBlockRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectProcessBlocklyBlockDefinitionRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
            CancellationToken cancellationToken = default)
        {
            return _repository.ListLatestAsync(_scope, cancellationToken);
        }

        public ValueTask<ProcessBlocklyBlockDefinitionRecord?> GetLatestAsync(
            string blockType,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetLatestAsync(_scope, blockType, cancellationToken);
        }

        public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsAsync(
            string blockType,
            CancellationToken cancellationToken = default)
        {
            return _repository.ListVersionsAsync(_scope, blockType, cancellationToken);
        }

        public ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
            string blockType,
            string category,
            string displayName,
            string blocklyJson,
            string executionMode,
            string runtimeActionContractSchemaVersion,
            string runtimeActionContractJson,
            string runtimeActionContractSha256,
            DateTimeOffset recordedAtUtc,
            CancellationToken cancellationToken = default)
        {
            return _repository.SaveNewVersionAsync(
                _scope,
                blockType,
                category,
                displayName,
                blocklyJson,
                executionMode,
                runtimeActionContractSchemaVersion,
                runtimeActionContractJson,
                runtimeActionContractSha256,
                recordedAtUtc,
                cancellationToken);
        }
    }
}
