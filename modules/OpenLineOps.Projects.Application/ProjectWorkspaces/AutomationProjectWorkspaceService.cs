using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Projects;

namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public sealed class AutomationProjectWorkspaceService : IAutomationProjectWorkspaceService
{
    private readonly IAutomationProjectRepository _repository;
    private readonly IAutomationProjectManifestStore _manifestStore;
    private readonly IClock _clock;

    public AutomationProjectWorkspaceService(
        IAutomationProjectRepository repository,
        IAutomationProjectManifestStore manifestStore,
        IClock clock)
    {
        _repository = repository;
        _manifestStore = manifestStore;
        _clock = clock;
    }

    public async Task<Result<AutomationProjectWorkspaceDetails>> CreateAsync(
        CreateAutomationProjectWorkspaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateCreateRequest(request);
        if (validation is not null)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(validation);
        }

        try
        {
            var projectPath = _manifestStore.GetProjectRootPath(request.ProjectPath);
            var existingManifest = await _manifestStore.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
            if (existingManifest is not null)
            {
                return Result.Failure<AutomationProjectWorkspaceDetails>(ApplicationError.Conflict(
                    "Projects.ManifestAlreadyExists",
                    $"Project manifest already exists at {_manifestStore.GetManifestPath(projectPath)}."));
            }

            var projectId = new AutomationProjectId(request.ProjectId);
            var existingProject = await _repository.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (existingProject is not null)
            {
                return Result.Failure<AutomationProjectWorkspaceDetails>(ApplicationError.Conflict(
                    "Projects.ProjectAlreadyExists",
                    $"Automation project {projectId} already exists."));
            }

            var project = AutomationProject.Create(
                projectId,
                request.DisplayName,
                projectPath,
                _clock.UtcNow);

            if (!string.IsNullOrWhiteSpace(request.DefaultApplicationId))
            {
                var addApplicationResult = project.AddApplication(ProjectApplication.Create(
                    new ProjectApplicationId(request.DefaultApplicationId),
                    request.DefaultApplicationName!,
                    AutomationProjectFileConvention.GetApplicationProjectRelativePath(
                        request.DefaultApplicationId)));
                if (!addApplicationResult.Succeeded)
                {
                    return Result.Failure<AutomationProjectWorkspaceDetails>(ApplicationError.Conflict(
                        addApplicationResult.Code,
                        addApplicationResult.Message));
                }
            }

            var manifest = AutomationProjectManifestMapper.FromProject(project, _clock.UtcNow);
            await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
            await _repository.SaveAsync(project, cancellationToken).ConfigureAwait(false);

            return Result.Success(ToWorkspaceDetails(project, manifest));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(InvalidInput(exception));
        }
        catch (InvalidDataException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(InvalidManifest(exception));
        }
        catch (IOException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(ManifestStorageFailed(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(ManifestStorageFailed(exception));
        }
    }

    public async Task<Result<AutomationProjectWorkspaceDetails>> OpenAsync(
        OpenAutomationProjectWorkspaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ProjectPath))
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(Required(
                "Projects.ProjectPathRequired",
                "ProjectPath"));
        }

        try
        {
            var projectPath = _manifestStore.GetProjectRootPath(request.ProjectPath);
            var manifest = await _manifestStore.LoadAsync(request.ProjectPath, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                return Result.Failure<AutomationProjectWorkspaceDetails>(ApplicationError.NotFound(
                    "Projects.ManifestNotFound",
                    $"Project manifest was not found at {_manifestStore.GetManifestPath(projectPath)}."));
            }

            manifest = manifest with
            {
                ProjectPath = projectPath
            };

            var project = AutomationProjectManifestMapper.ToProject(manifest, projectPath);
            await _repository.SaveAsync(project, cancellationToken).ConfigureAwait(false);

            return Result.Success(ToWorkspaceDetails(project, manifest));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(InvalidInput(exception));
        }
        catch (InvalidDataException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(InvalidManifest(exception));
        }
        catch (IOException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(ManifestStorageFailed(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(ManifestStorageFailed(exception));
        }
    }

    public async Task<Result<AutomationProjectWorkspaceDetails>> SaveManifestAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(Required(
                "Projects.ProjectIdRequired",
                "ProjectId"));
        }

        try
        {
            var project = await _repository
                .GetByIdAsync(new AutomationProjectId(projectId), cancellationToken)
                .ConfigureAwait(false);
            if (project is null)
            {
                return Result.Failure<AutomationProjectWorkspaceDetails>(ProjectNotFound(projectId));
            }

            var manifest = AutomationProjectManifestMapper.FromProject(project, _clock.UtcNow);
            await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);

            return Result.Success(ToWorkspaceDetails(project, manifest));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(InvalidInput(exception));
        }
        catch (IOException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(ManifestStorageFailed(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(ManifestStorageFailed(exception));
        }
    }

    public async Task<Result<AutomationProjectWorkspaceDetails>> ImportApplicationAsync(
        string projectId,
        ImportAutomationProjectApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(Required(
                "Projects.ProjectIdRequired",
                "ProjectId"));
        }

        if (string.IsNullOrWhiteSpace(request.ProjectFilePath))
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(Required(
                "Projects.ApplicationProjectFileRequired",
                "ProjectFilePath"));
        }

        try
        {
            var project = await _repository
                .GetByIdAsync(new AutomationProjectId(projectId), cancellationToken)
                .ConfigureAwait(false);
            if (project is null)
            {
                return Result.Failure<AutomationProjectWorkspaceDetails>(ProjectNotFound(projectId));
            }

            var applicationManifest = await _manifestStore
                .LoadApplicationProjectAsync(
                    project.ProjectPath,
                    request.ProjectFilePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (applicationManifest is null)
            {
                return Result.Failure<AutomationProjectWorkspaceDetails>(ApplicationError.NotFound(
                    "Projects.ApplicationProjectFileNotFound",
                    $"Application project file '{request.ProjectFilePath}' was not found."));
            }

            var detachedProject = AutomationProjectManifestMapper.ToProject(
                AutomationProjectManifestMapper.FromProject(project, _clock.UtcNow),
                project.ProjectPath);
            var addResult = detachedProject.AddApplication(ProjectApplication.Restore(
                new ProjectApplicationId(applicationManifest.ApplicationId),
                applicationManifest.DisplayName,
                string.IsNullOrWhiteSpace(applicationManifest.TopologyId)
                    ? null
                    : new AutomationTopologyId(applicationManifest.TopologyId),
                applicationManifest.ProcessDefinitionIds
                    .Select(processId => new ProcessDefinitionId(processId)),
                applicationManifest.ProjectFilePath));
            if (!addResult.Succeeded)
            {
                return Result.Failure<AutomationProjectWorkspaceDetails>(ApplicationError.Conflict(
                    addResult.Code,
                    addResult.Message));
            }

            var manifest = AutomationProjectManifestMapper.FromProject(detachedProject, _clock.UtcNow);
            await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
            await _repository.SaveAsync(detachedProject, cancellationToken).ConfigureAwait(false);

            return Result.Success(ToWorkspaceDetails(detachedProject, manifest));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(InvalidInput(exception));
        }
        catch (InvalidDataException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(InvalidManifest(exception));
        }
        catch (IOException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(ManifestStorageFailed(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure<AutomationProjectWorkspaceDetails>(ManifestStorageFailed(exception));
        }
    }

    private AutomationProjectWorkspaceDetails ToWorkspaceDetails(
        AutomationProject project,
        AutomationProjectManifest manifest)
    {
        return new AutomationProjectWorkspaceDetails(
            AutomationProjectMapper.ToDetails(project),
            _manifestStore.GetManifestPath(project.ProjectPath, project.Id.Value),
            manifest);
    }

    private static ApplicationError? ValidateCreateRequest(CreateAutomationProjectWorkspaceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return Required("Projects.ProjectIdRequired", "ProjectId");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Required("Projects.DisplayNameRequired", "DisplayName");
        }

        if (string.IsNullOrWhiteSpace(request.ProjectPath))
        {
            return Required("Projects.ProjectPathRequired", "ProjectPath");
        }

        if (!string.IsNullOrWhiteSpace(request.DefaultApplicationId)
            && string.IsNullOrWhiteSpace(request.DefaultApplicationName))
        {
            return Required("Projects.DefaultApplicationNameRequired", "DefaultApplicationName");
        }

        if (string.IsNullOrWhiteSpace(request.DefaultApplicationId)
            && !string.IsNullOrWhiteSpace(request.DefaultApplicationName))
        {
            return Required("Projects.DefaultApplicationIdRequired", "DefaultApplicationId");
        }

        return null;
    }

    private static ApplicationError Required(string code, string fieldName)
    {
        return ApplicationError.Validation(code, $"{fieldName} is required.");
    }

    private static ApplicationError InvalidInput(ArgumentException exception)
    {
        return ApplicationError.Validation(
            "Projects.InvalidWorkspaceInput",
            exception.Message);
    }

    private static ApplicationError InvalidManifest(InvalidDataException exception)
    {
        return ApplicationError.Validation(
            "Projects.InvalidProjectManifest",
            exception.Message);
    }

    private static ApplicationError ManifestStorageFailed(Exception exception)
    {
        return ApplicationError.Validation(
            "Projects.ProjectManifestStorageFailed",
            exception.Message);
    }

    private static ApplicationError ProjectNotFound(string projectId)
    {
        return ApplicationError.NotFound(
            "Projects.ProjectNotFound",
            $"Automation project {projectId} was not found.");
    }
}
