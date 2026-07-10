using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Engineering.Application.Configuration;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Snapshots;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Application.ProjectWorkspaces;

internal sealed class ProjectEngineeringConfigurationEngine
{
    private readonly ProjectApplicationWorkspaceScope _scope;
    private readonly IProjectEngineeringConfigurationRepository _repository;
    private readonly IClock _clock;

    public ProjectEngineeringConfigurationEngine(
        ProjectApplicationWorkspaceScope scope,
        IProjectEngineeringConfigurationRepository repository,
        IClock clock)
    {
        _scope = scope;
        _repository = repository;
        _clock = clock;
    }

    public async Task<Result<WorkspaceDetails>> CreateWorkspaceAsync(
        CreateWorkspaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateCreateWorkspaceRequest(request);
        if (validation is not null)
        {
            return Result.Failure<WorkspaceDetails>(validation);
        }

        try
        {
            var workspaceId = new WorkspaceId(request.WorkspaceId);
            var existing = await _repository
                .GetByIdAsync(_scope, workspaceId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return Result.Failure<WorkspaceDetails>(ApplicationError.Conflict(
                    "Engineering.WorkspaceAlreadyExists",
                    $"Workspace {workspaceId} already exists."));
            }

            var workspace = Workspace.Create(
                workspaceId,
                request.DisplayName,
                _clock.UtcNow);

            await _repository.SaveAsync(_scope, workspace, cancellationToken).ConfigureAwait(false);

            return Result.Success(EngineeringConfigurationMapper.ToDetails(workspace));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<WorkspaceDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<WorkspaceDetails>> GetWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await FindWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);

        return workspace is null
            ? Result.Failure<WorkspaceDetails>(WorkspaceNotFound(workspaceId))
            : Result.Success(EngineeringConfigurationMapper.ToDetails(workspace));
    }

    public async Task<Result<IReadOnlyCollection<WorkspaceDetails>>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default)
    {
        var workspaces = await _repository.ListWorkspacesAsync(_scope, cancellationToken).ConfigureAwait(false);
        var details = workspaces
            .OrderBy(workspace => workspace.Id.Value, StringComparer.Ordinal)
            .Select(EngineeringConfigurationMapper.ToDetails)
            .ToArray();

        return Result.Success<IReadOnlyCollection<WorkspaceDetails>>(details);
    }

    public async Task<Result<EngineeringProjectDetails>> CreateProjectAsync(
        CreateEngineeringProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateCreateProjectRequest(request);
        if (validation is not null)
        {
            return Result.Failure<EngineeringProjectDetails>(validation);
        }

        try
        {
            var projectId = new EngineeringProjectId(request.ProjectId);
            var workspaceId = new WorkspaceId(request.WorkspaceId);
            var workspace = await _repository
                .GetByIdAsync(_scope, workspaceId, cancellationToken)
                .ConfigureAwait(false);

            if (workspace is null)
            {
                return Result.Failure<EngineeringProjectDetails>(WorkspaceNotFound(request.WorkspaceId));
            }

            var existing = await _repository
                .GetByIdAsync(_scope, projectId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return Result.Failure<EngineeringProjectDetails>(ApplicationError.Conflict(
                    "Engineering.ProjectAlreadyExists",
                    $"Engineering project {projectId} already exists."));
            }

            var project = EngineeringProject.Create(
                projectId,
                workspaceId,
                request.DisplayName,
                _clock.UtcNow);

            await _repository.SaveAsync(_scope, project, cancellationToken).ConfigureAwait(false);

            return Result.Success(EngineeringConfigurationMapper.ToDetails(project));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<EngineeringProjectDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<EngineeringProjectDetails>> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        return project is null
            ? Result.Failure<EngineeringProjectDetails>(ProjectNotFound(projectId))
            : Result.Success(EngineeringConfigurationMapper.ToDetails(project));
    }

    public async Task<Result<IReadOnlyCollection<EngineeringProjectDetails>>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var projects = await _repository.ListProjectsAsync(_scope, cancellationToken).ConfigureAwait(false);
        var details = projects
            .OrderBy(project => project.Id.Value, StringComparer.Ordinal)
            .Select(EngineeringConfigurationMapper.ToDetails)
            .ToArray();

        return Result.Success<IReadOnlyCollection<EngineeringProjectDetails>>(details);
    }

    public async Task<Result<RecipeDetails>> CreateRecipeAsync(
        CreateRecipeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateCreateRecipeRequest(request);
        if (validation is not null)
        {
            return Result.Failure<RecipeDetails>(validation);
        }

        try
        {
            var recipeId = new RecipeId(request.RecipeId);
            var existing = await _repository
                .GetByIdAsync(_scope, recipeId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return Result.Failure<RecipeDetails>(ApplicationError.Conflict(
                    "Engineering.RecipeAlreadyExists",
                    $"Recipe {recipeId} already exists."));
            }

            var recipe = Recipe.Create(
                recipeId,
                new RecipeVersionId(request.VersionId),
                request.DisplayName,
                _clock.UtcNow);

            foreach (var parameterRequest in request.Parameters)
            {
                var parameterResult = recipe.AddOrUpdateParameter(
                    parameterRequest.Key,
                    parameterRequest.Value);
                if (!parameterResult.Succeeded)
                {
                    return Result.Failure<RecipeDetails>(ApplicationError.Validation(
                        parameterResult.Code,
                        parameterResult.Message));
                }
            }

            await _repository.SaveAsync(_scope, recipe, cancellationToken).ConfigureAwait(false);

            return Result.Success(EngineeringConfigurationMapper.ToDetails(recipe));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<RecipeDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<RecipeDetails>> GetRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        var recipe = await FindRecipeAsync(recipeId, cancellationToken).ConfigureAwait(false);

        return recipe is null
            ? Result.Failure<RecipeDetails>(RecipeNotFound(recipeId))
            : Result.Success(EngineeringConfigurationMapper.ToDetails(recipe));
    }

    public async Task<Result<IReadOnlyCollection<RecipeDetails>>> ListRecipesAsync(
        CancellationToken cancellationToken = default)
    {
        var recipes = await _repository.ListRecipesAsync(_scope, cancellationToken).ConfigureAwait(false);
        var details = recipes
            .OrderBy(recipe => recipe.Id.Value, StringComparer.Ordinal)
            .Select(EngineeringConfigurationMapper.ToDetails)
            .ToArray();

        return Result.Success<IReadOnlyCollection<RecipeDetails>>(details);
    }

    public async Task<Result<RecipeDetails>> PublishRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        var recipe = await FindRecipeAsync(recipeId, cancellationToken).ConfigureAwait(false);
        if (recipe is null)
        {
            return Result.Failure<RecipeDetails>(RecipeNotFound(recipeId));
        }

        var publishResult = recipe.Publish(_clock.UtcNow);
        if (!publishResult.Succeeded)
        {
            return Result.Failure<RecipeDetails>(ApplicationError.Conflict(
                publishResult.Code,
                publishResult.Message));
        }

        await _repository.SaveAsync(_scope, recipe, cancellationToken).ConfigureAwait(false);

        return Result.Success(EngineeringConfigurationMapper.ToDetails(recipe));
    }

    public async Task<Result<StationProfileDetails>> CreateStationProfileAsync(
        CreateStationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateCreateStationProfileRequest(request);
        if (validation is not null)
        {
            return Result.Failure<StationProfileDetails>(validation);
        }

        try
        {
            var stationProfileId = new StationProfileId(request.StationProfileId);
            var existing = await _repository
                .GetByIdAsync(_scope, stationProfileId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return Result.Failure<StationProfileDetails>(ApplicationError.Conflict(
                    "Engineering.StationProfileAlreadyExists",
                    $"Station profile {stationProfileId} already exists."));
            }

            var stationProfile = StationProfile.Create(
                stationProfileId,
                request.StationSystemId,
                request.DisplayName);

            foreach (var bindingRequest in request.DeviceBindings)
            {
                var bindingResult = stationProfile.AddDeviceBinding(DeviceBinding.Create(
                    new DeviceBindingId(bindingRequest.DeviceBindingId),
                    new DeviceCapabilityId(bindingRequest.CapabilityId),
                    bindingRequest.DeviceKey));

                if (!bindingResult.Succeeded)
                {
                    return Result.Failure<StationProfileDetails>(ApplicationError.Validation(
                        bindingResult.Code,
                        bindingResult.Message));
                }
            }

            await _repository.SaveAsync(_scope, stationProfile, cancellationToken).ConfigureAwait(false);

            return Result.Success(EngineeringConfigurationMapper.ToDetails(stationProfile));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<StationProfileDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<StationProfileDetails>> GetStationProfileAsync(
        string stationProfileId,
        CancellationToken cancellationToken = default)
    {
        var stationProfile = await FindStationProfileAsync(stationProfileId, cancellationToken)
            .ConfigureAwait(false);

        return stationProfile is null
            ? Result.Failure<StationProfileDetails>(StationProfileNotFound(stationProfileId))
            : Result.Success(EngineeringConfigurationMapper.ToDetails(stationProfile));
    }

    public async Task<Result<IReadOnlyCollection<StationProfileDetails>>> ListStationProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        var stationProfiles = await _repository.ListStationProfilesAsync(_scope, cancellationToken).ConfigureAwait(false);
        var details = stationProfiles
            .OrderBy(stationProfile => stationProfile.Id.Value, StringComparer.Ordinal)
            .Select(EngineeringConfigurationMapper.ToDetails)
            .ToArray();

        return Result.Success<IReadOnlyCollection<StationProfileDetails>>(details);
    }

    public async Task<Result<EngineeringProjectDetails>> PublishSnapshotAsync(
        string projectId,
        PublishConfigurationSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidatePublishSnapshotRequest(request);
        if (validation is not null)
        {
            return Result.Failure<EngineeringProjectDetails>(validation);
        }

        try
        {
            var project = await FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                return Result.Failure<EngineeringProjectDetails>(ProjectNotFound(projectId));
            }

            var recipe = await FindRecipeAsync(request.RecipeId, cancellationToken).ConfigureAwait(false);
            if (recipe is null)
            {
                return Result.Failure<EngineeringProjectDetails>(RecipeNotFound(request.RecipeId));
            }

            var stationProfile = await FindStationProfileAsync(request.StationProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (stationProfile is null)
            {
                return Result.Failure<EngineeringProjectDetails>(
                    StationProfileNotFound(request.StationProfileId));
            }

            var publishResult = project.PublishSnapshot(
                new ConfigurationSnapshotId(request.SnapshotId),
                new ProcessDefinitionId(request.ProcessDefinitionId),
                new ProcessVersionId(request.ProcessVersionId),
                recipe,
                stationProfile,
                _clock.UtcNow);

            if (!publishResult.Succeeded)
            {
                return Result.Failure<EngineeringProjectDetails>(ApplicationError.Conflict(
                    publishResult.Code,
                    publishResult.Message));
            }

            await _repository.SaveAsync(_scope, project, cancellationToken).ConfigureAwait(false);

            return Result.Success(EngineeringConfigurationMapper.ToDetails(project));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<EngineeringProjectDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<EngineeringProjectDetails>> RollbackSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return Result.Failure<EngineeringProjectDetails>(ApplicationError.Validation(
                "Engineering.SnapshotIdRequired",
                "SnapshotId is required."));
        }

        try
        {
            var project = await FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                return Result.Failure<EngineeringProjectDetails>(ProjectNotFound(projectId));
            }

            var rollbackResult = project.RollbackToSnapshot(new ConfigurationSnapshotId(snapshotId));

            if (!rollbackResult.Succeeded)
            {
                var error = rollbackResult.Code == "Engineering.SnapshotNotFound"
                    ? ApplicationError.NotFound(rollbackResult.Code, rollbackResult.Message)
                    : ApplicationError.Conflict(rollbackResult.Code, rollbackResult.Message);

                return Result.Failure<EngineeringProjectDetails>(error);
            }

            await _repository.SaveAsync(_scope, project, cancellationToken).ConfigureAwait(false);

            return Result.Success(EngineeringConfigurationMapper.ToDetails(project));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<EngineeringProjectDetails>(InvalidInput(exception));
        }
    }

    public async Task<Result<ConfigurationSnapshotDiffDetails>> CompareSnapshotsAsync(
        string projectId,
        string fromSnapshotId,
        string toSnapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fromSnapshotId))
        {
            return Result.Failure<ConfigurationSnapshotDiffDetails>(ApplicationError.Validation(
                "Engineering.FromSnapshotIdRequired",
                "FromSnapshotId is required."));
        }

        if (string.IsNullOrWhiteSpace(toSnapshotId))
        {
            return Result.Failure<ConfigurationSnapshotDiffDetails>(ApplicationError.Validation(
                "Engineering.ToSnapshotIdRequired",
                "ToSnapshotId is required."));
        }

        var project = await FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return Result.Failure<ConfigurationSnapshotDiffDetails>(ProjectNotFound(projectId));
        }

        var fromSnapshot = project.Snapshots.SingleOrDefault(snapshot =>
            string.Equals(snapshot.Id.Value, fromSnapshotId, StringComparison.Ordinal));
        if (fromSnapshot is null)
        {
            return Result.Failure<ConfigurationSnapshotDiffDetails>(
                SnapshotNotFound(projectId, fromSnapshotId));
        }

        var toSnapshot = project.Snapshots.SingleOrDefault(snapshot =>
            string.Equals(snapshot.Id.Value, toSnapshotId, StringComparison.Ordinal));
        if (toSnapshot is null)
        {
            return Result.Failure<ConfigurationSnapshotDiffDetails>(
                SnapshotNotFound(projectId, toSnapshotId));
        }

        return Result.Success(BuildDiff(project.Id.Value, fromSnapshot, toSnapshot));
    }

    private async Task<EngineeringProject?> FindProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return await _repository
            .GetByIdAsync(_scope, new EngineeringProjectId(projectId), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Workspace?> FindWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        return await _repository
            .GetByIdAsync(_scope, new WorkspaceId(workspaceId), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Recipe?> FindRecipeAsync(string recipeId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            return null;
        }

        return await _repository
            .GetByIdAsync(_scope, new RecipeId(recipeId), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StationProfile?> FindStationProfileAsync(
        string stationProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stationProfileId))
        {
            return null;
        }

        return await _repository
            .GetByIdAsync(_scope, new StationProfileId(stationProfileId), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ApplicationError? ValidateCreateWorkspaceRequest(CreateWorkspaceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            return Required("Engineering.WorkspaceIdRequired", "WorkspaceId");
        }

        return string.IsNullOrWhiteSpace(request.DisplayName)
            ? Required("Engineering.DisplayNameRequired", "DisplayName")
            : null;
    }

    private static ApplicationError? ValidateCreateProjectRequest(CreateEngineeringProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return Required("Engineering.ProjectIdRequired", "ProjectId");
        }

        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            return Required("Engineering.WorkspaceIdRequired", "WorkspaceId");
        }

        return string.IsNullOrWhiteSpace(request.DisplayName)
            ? Required("Engineering.DisplayNameRequired", "DisplayName")
            : null;
    }

    private static ApplicationError? ValidateCreateRecipeRequest(CreateRecipeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RecipeId))
        {
            return Required("Engineering.RecipeIdRequired", "RecipeId");
        }

        if (string.IsNullOrWhiteSpace(request.VersionId))
        {
            return Required("Engineering.VersionIdRequired", "VersionId");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Required("Engineering.DisplayNameRequired", "DisplayName");
        }

        if (request.Parameters is null)
        {
            return ApplicationError.Validation(
                "Engineering.ParametersRequired",
                "Parameters collection is required.");
        }

        foreach (var parameter in request.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Key) || string.IsNullOrWhiteSpace(parameter.Value))
            {
                return ApplicationError.Validation(
                    "Engineering.InvalidRecipeParameter",
                    "Recipe parameter key and value are required.");
            }
        }

        return null;
    }

    private static ApplicationError? ValidateCreateStationProfileRequest(
        CreateStationProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StationProfileId))
        {
            return Required("Engineering.StationProfileIdRequired", "StationProfileId");
        }

        if (string.IsNullOrWhiteSpace(request.StationSystemId))
        {
            return Required("Engineering.StationSystemIdRequired", "StationSystemId");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Required("Engineering.DisplayNameRequired", "DisplayName");
        }

        if (request.DeviceBindings is null)
        {
            return ApplicationError.Validation(
                "Engineering.DeviceBindingsRequired",
                "DeviceBindings collection is required.");
        }

        foreach (var binding in request.DeviceBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.DeviceBindingId)
                || string.IsNullOrWhiteSpace(binding.CapabilityId)
                || string.IsNullOrWhiteSpace(binding.DeviceKey))
            {
                return ApplicationError.Validation(
                    "Engineering.InvalidDeviceBinding",
                    "Device binding id, capability id, and device key are required.");
            }
        }

        return null;
    }

    private static ApplicationError? ValidatePublishSnapshotRequest(
        PublishConfigurationSnapshotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SnapshotId))
        {
            return Required("Engineering.SnapshotIdRequired", "SnapshotId");
        }

        if (string.IsNullOrWhiteSpace(request.ProcessDefinitionId))
        {
            return Required("Engineering.ProcessDefinitionIdRequired", "ProcessDefinitionId");
        }

        if (string.IsNullOrWhiteSpace(request.ProcessVersionId))
        {
            return Required("Engineering.ProcessVersionIdRequired", "ProcessVersionId");
        }

        if (string.IsNullOrWhiteSpace(request.RecipeId))
        {
            return Required("Engineering.RecipeIdRequired", "RecipeId");
        }

        return string.IsNullOrWhiteSpace(request.StationProfileId)
            ? Required("Engineering.StationProfileIdRequired", "StationProfileId")
            : null;
    }

    private static ApplicationError Required(string code, string fieldName)
    {
        return ApplicationError.Validation(code, $"{fieldName} is required.");
    }

    private static ApplicationError InvalidInput(ArgumentException exception)
    {
        return ApplicationError.Validation(
            "Engineering.InvalidConfigurationInput",
            exception.Message);
    }

    private static ApplicationError ProjectNotFound(string projectId)
    {
        return ApplicationError.NotFound(
            "Engineering.ProjectNotFound",
            $"Engineering project {projectId} was not found.");
    }

    private static ApplicationError WorkspaceNotFound(string workspaceId)
    {
        return ApplicationError.NotFound(
            "Engineering.WorkspaceNotFound",
            $"Workspace {workspaceId} was not found.");
    }

    private static ApplicationError RecipeNotFound(string recipeId)
    {
        return ApplicationError.NotFound(
            "Engineering.RecipeNotFound",
            $"Recipe {recipeId} was not found.");
    }

    private static ApplicationError StationProfileNotFound(string stationProfileId)
    {
        return ApplicationError.NotFound(
            "Engineering.StationProfileNotFound",
            $"Station profile {stationProfileId} was not found.");
    }

    private static ApplicationError SnapshotNotFound(string projectId, string snapshotId)
    {
        return ApplicationError.NotFound(
            "Engineering.SnapshotNotFound",
            $"Configuration snapshot {snapshotId} was not found in project {projectId}.");
    }

    private static ConfigurationSnapshotDiffDetails BuildDiff(
        string projectId,
        ConfigurationSnapshot fromSnapshot,
        ConfigurationSnapshot toSnapshot)
    {
        var changes = new List<ConfigurationSnapshotDiffItemDetails>();

        AddChanged(
            changes,
            "Process",
            "ProcessDefinitionId",
            fromSnapshot.ProcessDefinitionId.Value,
            toSnapshot.ProcessDefinitionId.Value);
        AddChanged(
            changes,
            "Process",
            "ProcessVersionId",
            fromSnapshot.ProcessVersionId.Value,
            toSnapshot.ProcessVersionId.Value);
        AddChanged(
            changes,
            "Recipe",
            "RecipeId",
            fromSnapshot.RecipeId.Value,
            toSnapshot.RecipeId.Value);
        AddChanged(
            changes,
            "Recipe",
            "RecipeVersionId",
            fromSnapshot.RecipeVersionId.Value,
            toSnapshot.RecipeVersionId.Value);
        AddChanged(
            changes,
            "Station",
            "StationProfileId",
            fromSnapshot.StationProfileId.Value,
            toSnapshot.StationProfileId.Value);
        AddDeviceBindingChanges(changes, fromSnapshot, toSnapshot);

        return new ConfigurationSnapshotDiffDetails(
            projectId,
            fromSnapshot.Id.Value,
            toSnapshot.Id.Value,
            changes);
    }

    private static void AddDeviceBindingChanges(
        List<ConfigurationSnapshotDiffItemDetails> changes,
        ConfigurationSnapshot fromSnapshot,
        ConfigurationSnapshot toSnapshot)
    {
        var fromBindings = fromSnapshot.DeviceBindings.ToDictionary(
            binding => binding.DeviceBindingId.Value,
            StringComparer.Ordinal);
        var toBindings = toSnapshot.DeviceBindings.ToDictionary(
            binding => binding.DeviceBindingId.Value,
            StringComparer.Ordinal);
        var bindingIds = fromBindings.Keys
            .Union(toBindings.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var bindingId in bindingIds)
        {
            var hasFrom = fromBindings.TryGetValue(bindingId, out var fromBinding);
            var hasTo = toBindings.TryGetValue(bindingId, out var toBinding);

            if (!hasFrom && hasTo)
            {
                changes.Add(new ConfigurationSnapshotDiffItemDetails(
                    "DeviceBinding",
                    bindingId,
                    null,
                    FormatDeviceBinding(toBinding!),
                    "Added"));
                continue;
            }

            if (hasFrom && !hasTo)
            {
                changes.Add(new ConfigurationSnapshotDiffItemDetails(
                    "DeviceBinding",
                    bindingId,
                    FormatDeviceBinding(fromBinding!),
                    null,
                    "Removed"));
                continue;
            }

            AddChanged(
                changes,
                "DeviceBinding",
                $"{bindingId}.CapabilityId",
                fromBinding!.CapabilityId.Value,
                toBinding!.CapabilityId.Value);
            AddChanged(
                changes,
                "DeviceBinding",
                $"{bindingId}.DeviceKey",
                fromBinding.DeviceKey,
                toBinding.DeviceKey);
        }
    }

    private static void AddChanged(
        List<ConfigurationSnapshotDiffItemDetails> changes,
        string area,
        string field,
        string fromValue,
        string toValue)
    {
        if (string.Equals(fromValue, toValue, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(new ConfigurationSnapshotDiffItemDetails(
            area,
            field,
            fromValue,
            toValue,
            "Changed"));
    }

    private static string FormatDeviceBinding(DeviceBindingSnapshot binding)
    {
        return $"{binding.CapabilityId.Value}:{binding.DeviceKey}";
    }
}
