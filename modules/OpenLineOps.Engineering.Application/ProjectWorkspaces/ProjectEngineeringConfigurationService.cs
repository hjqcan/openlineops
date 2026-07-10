using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Engineering.Application.Configuration;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Application.ProjectWorkspaces;

public sealed class ProjectEngineeringConfigurationService : IProjectEngineeringConfigurationService
{
    private readonly IProjectApplicationWorkspaceScopeResolver _scopeResolver;
    private readonly IProjectEngineeringConfigurationRepository _repository;
    private readonly IClock _clock;

    public ProjectEngineeringConfigurationService(
        IProjectApplicationWorkspaceScopeResolver scopeResolver,
        IProjectEngineeringConfigurationRepository repository,
        IClock clock)
    {
        _scopeResolver = scopeResolver;
        _repository = repository;
        _clock = clock;
    }

    public Task<Result<WorkspaceDetails>> CreateWorkspaceAsync(
        string projectId,
        string applicationId,
        CreateWorkspaceRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.CreateWorkspaceAsync(request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<WorkspaceDetails>> GetWorkspaceAsync(
        string projectId,
        string applicationId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.GetWorkspaceAsync(workspaceId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<IReadOnlyCollection<WorkspaceDetails>>> ListWorkspacesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.ListWorkspacesAsync(cancellationToken),
            cancellationToken);
    }

    public Task<Result<EngineeringProjectDetails>> CreateProjectAsync(
        string projectId,
        string applicationId,
        CreateEngineeringProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.CreateProjectAsync(request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<EngineeringProjectDetails>> GetProjectAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.GetProjectAsync(engineeringProjectId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<IReadOnlyCollection<EngineeringProjectDetails>>> ListProjectsAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.ListProjectsAsync(cancellationToken),
            cancellationToken);
    }

    public Task<Result<RecipeDetails>> CreateRecipeAsync(
        string projectId,
        string applicationId,
        CreateRecipeRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.CreateRecipeAsync(request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<RecipeDetails>> GetRecipeAsync(
        string projectId,
        string applicationId,
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.GetRecipeAsync(recipeId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<IReadOnlyCollection<RecipeDetails>>> ListRecipesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.ListRecipesAsync(cancellationToken),
            cancellationToken);
    }

    public Task<Result<RecipeDetails>> PublishRecipeAsync(
        string projectId,
        string applicationId,
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.PublishRecipeAsync(recipeId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<StationProfileDetails>> CreateStationProfileAsync(
        string projectId,
        string applicationId,
        CreateStationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.CreateStationProfileAsync(request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<StationProfileDetails>> GetStationProfileAsync(
        string projectId,
        string applicationId,
        string stationProfileId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.GetStationProfileAsync(stationProfileId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<IReadOnlyCollection<StationProfileDetails>>> ListStationProfilesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.ListStationProfilesAsync(cancellationToken),
            cancellationToken);
    }

    public Task<Result<EngineeringProjectDetails>> PublishSnapshotAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        PublishConfigurationSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.PublishSnapshotAsync(engineeringProjectId, request, cancellationToken),
            cancellationToken);
    }

    public Task<Result<EngineeringProjectDetails>> RollbackSnapshotAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.RollbackSnapshotAsync(engineeringProjectId, snapshotId, cancellationToken),
            cancellationToken);
    }

    public Task<Result<ConfigurationSnapshotDiffDetails>> CompareSnapshotsAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        string fromSnapshotId,
        string toSnapshotId,
        CancellationToken cancellationToken = default)
    {
        return InScopeAsync(
            projectId,
            applicationId,
            service => service.CompareSnapshotsAsync(
                engineeringProjectId,
                fromSnapshotId,
                toSnapshotId,
                cancellationToken),
            cancellationToken);
    }

    private async Task<Result<T>> InScopeAsync<T>(
        string projectId,
        string applicationId,
        Func<IEngineeringConfigurationService, Task<Result<T>>> execute,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(applicationId))
        {
            return Result.Failure<T>(ApplicationError.Validation(
                "Engineering.ProjectApplicationScopeRequired",
                "ProjectId and ApplicationId are required."));
        }

        var scope = await _scopeResolver
            .ResolveAsync(projectId, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            return Result.Failure<T>(ApplicationError.NotFound(
                "Engineering.ProjectApplicationNotFound",
                $"Application {applicationId} was not found in project {projectId}."));
        }

        var service = new EngineeringConfigurationService(
            new ScopedWorkspaceRepository(scope, _repository),
            new ScopedEngineeringProjectRepository(scope, _repository),
            new ScopedRecipeRepository(scope, _repository),
            new ScopedStationProfileRepository(scope, _repository),
            _clock);

        return await execute(service).ConfigureAwait(false);
    }

    private sealed class ScopedWorkspaceRepository : IWorkspaceRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectEngineeringConfigurationRepository _repository;

        public ScopedWorkspaceRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectEngineeringConfigurationRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public Task SaveAsync(Workspace workspace, CancellationToken cancellationToken = default)
        {
            return _repository.SaveAsync(_scope, workspace, cancellationToken);
        }

        public Task<Workspace?> GetByIdAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetByIdAsync(_scope, workspaceId, cancellationToken);
        }

        public Task<IReadOnlyCollection<Workspace>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return _repository.ListWorkspacesAsync(_scope, cancellationToken);
        }
    }

    private sealed class ScopedEngineeringProjectRepository : IEngineeringProjectRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectEngineeringConfigurationRepository _repository;

        public ScopedEngineeringProjectRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectEngineeringConfigurationRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public Task SaveAsync(
            EngineeringProject project,
            CancellationToken cancellationToken = default)
        {
            return _repository.SaveAsync(_scope, project, cancellationToken);
        }

        public Task<EngineeringProject?> GetByIdAsync(
            EngineeringProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetByIdAsync(_scope, projectId, cancellationToken);
        }

        public Task<IReadOnlyCollection<EngineeringProject>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return _repository.ListProjectsAsync(_scope, cancellationToken);
        }
    }

    private sealed class ScopedRecipeRepository : IRecipeRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectEngineeringConfigurationRepository _repository;

        public ScopedRecipeRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectEngineeringConfigurationRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
        {
            return _repository.SaveAsync(_scope, recipe, cancellationToken);
        }

        public Task<Recipe?> GetByIdAsync(
            RecipeId recipeId,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetByIdAsync(_scope, recipeId, cancellationToken);
        }

        public Task<IReadOnlyCollection<Recipe>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return _repository.ListRecipesAsync(_scope, cancellationToken);
        }
    }

    private sealed class ScopedStationProfileRepository : IStationProfileRepository
    {
        private readonly ProjectApplicationWorkspaceScope _scope;
        private readonly IProjectEngineeringConfigurationRepository _repository;

        public ScopedStationProfileRepository(
            ProjectApplicationWorkspaceScope scope,
            IProjectEngineeringConfigurationRepository repository)
        {
            _scope = scope;
            _repository = repository;
        }

        public Task SaveAsync(
            StationProfile stationProfile,
            CancellationToken cancellationToken = default)
        {
            return _repository.SaveAsync(_scope, stationProfile, cancellationToken);
        }

        public Task<StationProfile?> GetByIdAsync(
            StationProfileId stationProfileId,
            CancellationToken cancellationToken = default)
        {
            return _repository.GetByIdAsync(_scope, stationProfileId, cancellationToken);
        }

        public Task<IReadOnlyCollection<StationProfile>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return _repository.ListStationProfilesAsync(_scope, cancellationToken);
        }
    }
}
