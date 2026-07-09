using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Operations;
using OpenLineOps.Projects.Domain.Projects;
using OpenLineOps.Projects.Domain.Snapshots;

namespace OpenLineOps.Projects.Application.Projects;

public sealed class AutomationProjectService : IAutomationProjectService
{
    private readonly IAutomationProjectRepository _repository;
    private readonly IClock _clock;

    public AutomationProjectService(IAutomationProjectRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<Result<AutomationProjectDetails>> CreateAsync(
        CreateAutomationProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateCreateRequest(request);
        if (validation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(validation);
        }

        try
        {
            var projectId = new AutomationProjectId(request.ProjectId);
            var existing = await _repository.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return Result.Failure<AutomationProjectDetails>(ApplicationError.Conflict(
                    "Projects.ProjectAlreadyExists",
                    $"Automation project {projectId} already exists."));
            }

            var project = AutomationProject.Create(
                projectId,
                request.DisplayName,
                request.ProjectPath,
                _clock.UtcNow);

            await _repository.SaveAsync(project, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationProjectMapper.ToDetails(project));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<AutomationProjectDetails>> GetByIdAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        return project is null
            ? Result.Failure<AutomationProjectDetails>(ProjectNotFound(projectId))
            : Result.Success(AutomationProjectMapper.ToDetails(project));
    }

    public async Task<Result<IReadOnlyCollection<AutomationProjectSummary>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var projects = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        var summaries = projects
            .OrderBy(project => project.Id.Value, StringComparer.Ordinal)
            .Select(AutomationProjectMapper.ToSummary)
            .ToArray();

        return Result.Success<IReadOnlyCollection<AutomationProjectSummary>>(summaries);
    }

    public async Task<Result<AutomationProjectDetails>> AddApplicationAsync(
        string projectId,
        AddProjectApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return Result.Failure<AutomationProjectDetails>(Required("Projects.ApplicationIdRequired", "ApplicationId"));
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Result.Failure<AutomationProjectDetails>(Required("Projects.DisplayNameRequired", "DisplayName"));
        }

        try
        {
            var project = await FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                return Result.Failure<AutomationProjectDetails>(ProjectNotFound(projectId));
            }

            var result = project.AddApplication(ProjectApplication.Create(
                new ProjectApplicationId(request.ApplicationId),
                request.DisplayName));
            if (!result.Succeeded)
            {
                return Result.Failure<AutomationProjectDetails>(ToConflict(result));
            }

            await _repository.SaveAsync(project, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationProjectMapper.ToDetails(project));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<AutomationProjectDetails>> LinkTopologyAsync(
        string projectId,
        LinkProjectTopologyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return Result.Failure<AutomationProjectDetails>(Required("Projects.ApplicationIdRequired", "ApplicationId"));
        }

        if (string.IsNullOrWhiteSpace(request.TopologyId))
        {
            return Result.Failure<AutomationProjectDetails>(Required("Projects.TopologyIdRequired", "TopologyId"));
        }

        return await MutateProjectAsync(
            projectId,
            project => project.LinkTopology(
                new ProjectApplicationId(request.ApplicationId),
                new AutomationTopologyId(request.TopologyId)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationProjectDetails>> LinkProcessDefinitionAsync(
        string projectId,
        LinkProjectProcessDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return Result.Failure<AutomationProjectDetails>(Required("Projects.ApplicationIdRequired", "ApplicationId"));
        }

        if (string.IsNullOrWhiteSpace(request.ProcessDefinitionId))
        {
            return Result.Failure<AutomationProjectDetails>(Required("Projects.ProcessDefinitionIdRequired", "ProcessDefinitionId"));
        }

        return await MutateProjectAsync(
            projectId,
            project => project.LinkProcessDefinition(
                new ProjectApplicationId(request.ApplicationId),
                new ProcessDefinitionId(request.ProcessDefinitionId)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<AutomationProjectDetails>> PublishSnapshotAsync(
        string projectId,
        PublishProjectSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidatePublishSnapshotRequest(request);
        if (validation is not null)
        {
            return Result.Failure<AutomationProjectDetails>(validation);
        }

        try
        {
            var project = await FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                return Result.Failure<AutomationProjectDetails>(ProjectNotFound(projectId));
            }

            var result = project.PublishSnapshot(
                new PublishedProjectSnapshotId(request.SnapshotId),
                new ProjectApplicationId(request.ApplicationId),
                new AutomationTopologyId(request.TopologyId),
                new ProcessDefinitionId(request.ProcessDefinitionId),
                new ProcessVersionId(request.ProcessVersionId),
                new ConfigurationSnapshotId(request.ConfigurationSnapshotId),
                request.CapabilityBindings.Select(binding => new SnapshotCapabilityBinding(
                    binding.CapabilityId,
                    binding.BindingId,
                    binding.ProviderKind,
                    binding.ProviderKey)),
                request.TargetReferences.Select(target => new ProjectTargetReference(target.Kind, target.TargetId)),
                request.BlockVersionIds,
                _clock.UtcNow);
            if (!result.Succeeded)
            {
                return Result.Failure<AutomationProjectDetails>(ToConflict(result));
            }

            await _repository.SaveAsync(project, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationProjectMapper.ToDetails(project));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectDetails>(InvalidInput(exception));
        }
    }

    private async Task<Result<AutomationProjectDetails>> MutateProjectAsync(
        string projectId,
        Func<AutomationProject, ProjectOperationResult> mutate,
        CancellationToken cancellationToken)
    {
        try
        {
            var project = await FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                return Result.Failure<AutomationProjectDetails>(ProjectNotFound(projectId));
            }

            var result = mutate(project);
            if (!result.Succeeded)
            {
                return Result.Failure<AutomationProjectDetails>(ToConflict(result));
            }

            await _repository.SaveAsync(project, cancellationToken).ConfigureAwait(false);

            return Result.Success(AutomationProjectMapper.ToDetails(project));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<AutomationProjectDetails>(InvalidInput(exception));
        }
    }

    private async Task<AutomationProject?> FindProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return await _repository
            .GetByIdAsync(new AutomationProjectId(projectId), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ApplicationError? ValidateCreateRequest(CreateAutomationProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return Required("Projects.ProjectIdRequired", "ProjectId");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Required("Projects.DisplayNameRequired", "DisplayName");
        }

        return string.IsNullOrWhiteSpace(request.ProjectPath)
            ? Required("Projects.ProjectPathRequired", "ProjectPath")
            : null;
    }

    private static ApplicationError? ValidatePublishSnapshotRequest(PublishProjectSnapshotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SnapshotId))
        {
            return Required("Projects.SnapshotIdRequired", "SnapshotId");
        }

        if (string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return Required("Projects.ApplicationIdRequired", "ApplicationId");
        }

        if (string.IsNullOrWhiteSpace(request.TopologyId))
        {
            return Required("Projects.TopologyIdRequired", "TopologyId");
        }

        if (string.IsNullOrWhiteSpace(request.ProcessDefinitionId))
        {
            return Required("Projects.ProcessDefinitionIdRequired", "ProcessDefinitionId");
        }

        if (string.IsNullOrWhiteSpace(request.ProcessVersionId))
        {
            return Required("Projects.ProcessVersionIdRequired", "ProcessVersionId");
        }

        if (string.IsNullOrWhiteSpace(request.ConfigurationSnapshotId))
        {
            return Required("Projects.ConfigurationSnapshotIdRequired", "ConfigurationSnapshotId");
        }

        if (request.CapabilityBindings is null)
        {
            return ApplicationError.Validation(
                "Projects.CapabilityBindingsRequired",
                "CapabilityBindings collection is required.");
        }

        if (request.TargetReferences is null)
        {
            return ApplicationError.Validation(
                "Projects.TargetReferencesRequired",
                "TargetReferences collection is required.");
        }

        return request.BlockVersionIds is null
            ? ApplicationError.Validation(
                "Projects.BlockVersionIdsRequired",
                "BlockVersionIds collection is required.")
            : null;
    }

    private static ApplicationError Required(string code, string fieldName)
    {
        return ApplicationError.Validation(code, $"{fieldName} is required.");
    }

    private static ApplicationError ToConflict(ProjectOperationResult result)
    {
        return ApplicationError.Conflict(result.Code, result.Message);
    }

    private static ApplicationError InvalidInput(ArgumentException exception)
    {
        return ApplicationError.Validation(
            "Projects.InvalidProjectInput",
            exception.Message);
    }

    private static ApplicationError ProjectNotFound(string projectId)
    {
        return ApplicationError.NotFound(
            "Projects.ProjectNotFound",
            $"Automation project {projectId} was not found.");
    }
}
