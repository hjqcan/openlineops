using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class FileSystemProjectEngineeringConfigurationRepository :
    IProjectEngineeringConfigurationRepository
{
    public Task SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return SaveDocumentAsync(
            scope,
            ProjectEngineeringResourcePath.GetWorkspacePath(scope, workspace.Id.Value),
            ProjectEngineeringResourceKinds.Workspace,
            workspace.Id.Value,
            EngineeringSnapshotMapper.ToSnapshot(workspace),
            snapshot => snapshot.WorkspaceId,
            ValidateWorkspaceSnapshot,
            ProjectEngineeringResourcePath.GetWorkspacePath,
            cancellationToken);
    }

    public Task<Workspace?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);

        return LoadAggregateAsync<PersistedWorkspace, Workspace>(
            scope,
            ProjectEngineeringResourcePath.GetWorkspacePath(scope, workspaceId.Value),
            ProjectEngineeringResourceKinds.Workspace,
            workspaceId.Value,
            snapshot => snapshot.WorkspaceId,
            ValidateWorkspaceSnapshot,
            ProjectEngineeringResourcePath.GetWorkspacePath,
            snapshot => EngineeringSnapshotMapper.ToAggregate(snapshot),
            cancellationToken);
    }

    public Task<IReadOnlyCollection<Workspace>> ListWorkspacesAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        return ListAggregatesAsync<PersistedWorkspace, Workspace>(
            scope,
            ProjectEngineeringResourcePath.GetWorkspacesDirectory(scope),
            ProjectEngineeringResourceKinds.Workspace,
            snapshot => snapshot.WorkspaceId,
            ValidateWorkspaceSnapshot,
            ProjectEngineeringResourcePath.GetWorkspacePath,
            snapshot => EngineeringSnapshotMapper.ToAggregate(snapshot),
            cancellationToken);
    }

    public Task SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        EngineeringProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        return SaveDocumentAsync(
            scope,
            ProjectEngineeringResourcePath.GetProjectPath(scope, project.Id.Value),
            ProjectEngineeringResourceKinds.Project,
            project.Id.Value,
            EngineeringSnapshotMapper.ToSnapshot(project),
            snapshot => snapshot.ProjectId,
            ValidateProjectSnapshot,
            ProjectEngineeringResourcePath.GetProjectPath,
            cancellationToken);
    }

    public Task<EngineeringProject?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        EngineeringProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectId);

        return LoadAggregateAsync<PersistedEngineeringProject, EngineeringProject>(
            scope,
            ProjectEngineeringResourcePath.GetProjectPath(scope, projectId.Value),
            ProjectEngineeringResourceKinds.Project,
            projectId.Value,
            snapshot => snapshot.ProjectId,
            ValidateProjectSnapshot,
            ProjectEngineeringResourcePath.GetProjectPath,
            snapshot => EngineeringSnapshotMapper.ToAggregate(snapshot),
            cancellationToken);
    }

    public Task<IReadOnlyCollection<EngineeringProject>> ListProjectsAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        return ListAggregatesAsync<PersistedEngineeringProject, EngineeringProject>(
            scope,
            ProjectEngineeringResourcePath.GetProjectsDirectory(scope),
            ProjectEngineeringResourceKinds.Project,
            snapshot => snapshot.ProjectId,
            ValidateProjectSnapshot,
            ProjectEngineeringResourcePath.GetProjectPath,
            snapshot => EngineeringSnapshotMapper.ToAggregate(snapshot),
            cancellationToken);
    }

    public Task SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        Recipe recipe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        return SaveDocumentAsync(
            scope,
            ProjectEngineeringResourcePath.GetRecipePath(scope, recipe.Id.Value),
            ProjectEngineeringResourceKinds.Recipe,
            recipe.Id.Value,
            EngineeringSnapshotMapper.ToSnapshot(recipe),
            snapshot => snapshot.RecipeId,
            ValidateRecipeSnapshot,
            ProjectEngineeringResourcePath.GetRecipePath,
            cancellationToken);
    }

    public Task<Recipe?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        RecipeId recipeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeId);

        return LoadAggregateAsync<PersistedRecipe, Recipe>(
            scope,
            ProjectEngineeringResourcePath.GetRecipePath(scope, recipeId.Value),
            ProjectEngineeringResourceKinds.Recipe,
            recipeId.Value,
            snapshot => snapshot.RecipeId,
            ValidateRecipeSnapshot,
            ProjectEngineeringResourcePath.GetRecipePath,
            snapshot => EngineeringSnapshotMapper.ToAggregate(snapshot),
            cancellationToken);
    }

    public Task<IReadOnlyCollection<Recipe>> ListRecipesAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        return ListAggregatesAsync<PersistedRecipe, Recipe>(
            scope,
            ProjectEngineeringResourcePath.GetRecipesDirectory(scope),
            ProjectEngineeringResourceKinds.Recipe,
            snapshot => snapshot.RecipeId,
            ValidateRecipeSnapshot,
            ProjectEngineeringResourcePath.GetRecipePath,
            snapshot => EngineeringSnapshotMapper.ToAggregate(snapshot),
            cancellationToken);
    }

    public Task SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        StationProfile stationProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationProfile);

        return SaveDocumentAsync(
            scope,
            ProjectEngineeringResourcePath.GetStationProfilePath(scope, stationProfile.Id.Value),
            ProjectEngineeringResourceKinds.StationProfile,
            stationProfile.Id.Value,
            EngineeringSnapshotMapper.ToSnapshot(stationProfile),
            snapshot => snapshot.StationProfileId,
            ValidateStationProfileSnapshot,
            ProjectEngineeringResourcePath.GetStationProfilePath,
            cancellationToken);
    }

    public Task<StationProfile?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        StationProfileId stationProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationProfileId);

        return LoadAggregateAsync<PersistedStationProfile, StationProfile>(
            scope,
            ProjectEngineeringResourcePath.GetStationProfilePath(scope, stationProfileId.Value),
            ProjectEngineeringResourceKinds.StationProfile,
            stationProfileId.Value,
            snapshot => snapshot.StationProfileId,
            ValidateStationProfileSnapshot,
            ProjectEngineeringResourcePath.GetStationProfilePath,
            snapshot => EngineeringSnapshotMapper.ToAggregate(snapshot),
            cancellationToken);
    }

    public Task<IReadOnlyCollection<StationProfile>> ListStationProfilesAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        return ListAggregatesAsync<PersistedStationProfile, StationProfile>(
            scope,
            ProjectEngineeringResourcePath.GetStationProfilesDirectory(scope),
            ProjectEngineeringResourceKinds.StationProfile,
            snapshot => snapshot.StationProfileId,
            ValidateStationProfileSnapshot,
            ProjectEngineeringResourcePath.GetStationProfilePath,
            snapshot => EngineeringSnapshotMapper.ToAggregate(snapshot),
            cancellationToken);
    }

    private static async Task SaveDocumentAsync<TSnapshot>(
        ProjectApplicationWorkspaceScope scope,
        string path,
        string resourceKind,
        string resourceId,
        TSnapshot snapshot,
        Func<TSnapshot, string> getSnapshotId,
        Action<string, TSnapshot> validateSnapshot,
        Func<ProjectApplicationWorkspaceScope, string, string> getExpectedPath,
        CancellationToken cancellationToken)
        where TSnapshot : class
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var document = new ProjectEngineeringConfigurationDocument<TSnapshot>(
            ProjectEngineeringConfigurationDocumentSchema.CurrentSchema,
            ProjectEngineeringConfigurationDocumentSchema.CurrentSchemaVersion,
            scope.ApplicationId,
            resourceKind,
            resourceId,
            snapshot);

        ValidateDocument(
            scope,
            path,
            document,
            resourceKind,
            resourceId,
            getSnapshotId,
            validateSnapshot,
            getExpectedPath);
        await ProjectEngineeringResourceFileStore
            .SaveJsonAsync(path, document, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<TAggregate?> LoadAggregateAsync<TSnapshot, TAggregate>(
        ProjectApplicationWorkspaceScope scope,
        string path,
        string resourceKind,
        string resourceId,
        Func<TSnapshot, string> getSnapshotId,
        Action<string, TSnapshot> validateSnapshot,
        Func<ProjectApplicationWorkspaceScope, string, string> getExpectedPath,
        Func<TSnapshot, TAggregate> toAggregate,
        CancellationToken cancellationToken)
        where TSnapshot : class
        where TAggregate : class
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var document = await ProjectEngineeringResourceFileStore
            .LoadJsonAsync<ProjectEngineeringConfigurationDocument<TSnapshot>>(path, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        ValidateDocument(
            scope,
            path,
            document,
            resourceKind,
            resourceId,
            getSnapshotId,
            validateSnapshot,
            getExpectedPath);
        return RestoreAggregate(path, document.Snapshot, toAggregate);
    }

    private static async Task<IReadOnlyCollection<TAggregate>> ListAggregatesAsync<TSnapshot, TAggregate>(
        ProjectApplicationWorkspaceScope scope,
        string directory,
        string resourceKind,
        Func<TSnapshot, string> getSnapshotId,
        Action<string, TSnapshot> validateSnapshot,
        Func<ProjectApplicationWorkspaceScope, string, string> getExpectedPath,
        Func<TSnapshot, TAggregate> toAggregate,
        CancellationToken cancellationToken)
        where TSnapshot : class
        where TAggregate : class
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var aggregates = new List<TAggregate>();
        foreach (var path in Directory
                     .EnumerateFiles(directory, $"{resourceKind}-*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await ProjectEngineeringResourceFileStore
                .LoadJsonAsync<ProjectEngineeringConfigurationDocument<TSnapshot>>(path, cancellationToken)
                .ConfigureAwait(false)
                ?? throw InvalidResource(path, "document is empty");

            ValidateDocument(
                scope,
                path,
                document,
                resourceKind,
                expectedResourceId: null,
                getSnapshotId,
                validateSnapshot,
                getExpectedPath);
            aggregates.Add(RestoreAggregate(path, document.Snapshot, toAggregate));
        }

        return aggregates;
    }

    private static void ValidateDocument<TSnapshot>(
        ProjectApplicationWorkspaceScope scope,
        string path,
        ProjectEngineeringConfigurationDocument<TSnapshot> document,
        string expectedResourceKind,
        string? expectedResourceId,
        Func<TSnapshot, string> getSnapshotId,
        Action<string, TSnapshot> validateSnapshot,
        Func<ProjectApplicationWorkspaceScope, string, string> getExpectedPath)
        where TSnapshot : class
    {
        if (!string.Equals(
                document.Schema,
                ProjectEngineeringConfigurationDocumentSchema.CurrentSchema,
                StringComparison.Ordinal))
        {
            throw InvalidResource(path, $"schema '{document.Schema}' is not supported");
        }

        if (document.SchemaVersion != ProjectEngineeringConfigurationDocumentSchema.CurrentSchemaVersion)
        {
            throw InvalidResource(path, $"schema version {document.SchemaVersion} is not supported");
        }

        if (!string.Equals(document.ApplicationId, scope.ApplicationId, StringComparison.Ordinal))
        {
            throw InvalidResource(
                path,
                $"application is {document.ApplicationId}, expected {scope.ApplicationId}");
        }

        if (!string.Equals(document.ResourceKind, expectedResourceKind, StringComparison.Ordinal))
        {
            throw InvalidResource(
                path,
                $"kind is '{document.ResourceKind}', expected '{expectedResourceKind}'");
        }

        if (string.IsNullOrWhiteSpace(document.ResourceId))
        {
            throw InvalidResource(path, "resource id is empty");
        }

        if (expectedResourceId is not null
            && !string.Equals(document.ResourceId, expectedResourceId, StringComparison.Ordinal))
        {
            throw InvalidResource(
                path,
                $"resource id is '{document.ResourceId}', expected '{expectedResourceId}'");
        }

        var expectedPath = getExpectedPath(scope, document.ResourceId);
        if (!string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResource(path, "resource identity does not match its file name");
        }

        if (document.Snapshot is null)
        {
            throw InvalidResource(path, "snapshot is empty");
        }

        var snapshotId = getSnapshotId(document.Snapshot);
        if (!string.Equals(snapshotId, document.ResourceId, StringComparison.Ordinal))
        {
            throw InvalidResource(
                path,
                $"nested snapshot id is '{snapshotId}', expected '{document.ResourceId}'");
        }

        validateSnapshot(path, document.Snapshot);
    }

    private static TAggregate RestoreAggregate<TSnapshot, TAggregate>(
        string path,
        TSnapshot snapshot,
        Func<TSnapshot, TAggregate> toAggregate)
    {
        try
        {
            return toAggregate(snapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"Project engineering resource '{path}' contains an invalid domain snapshot.",
                exception);
        }
    }

    private static void ValidateWorkspaceSnapshot(string path, PersistedWorkspace snapshot)
    {
        Require(path, snapshot.WorkspaceId, "workspace id");
        Require(path, snapshot.DisplayName, "workspace display name");
    }

    private static void ValidateProjectSnapshot(string path, PersistedEngineeringProject snapshot)
    {
        Require(path, snapshot.ProjectId, "engineering project id");
        Require(path, snapshot.WorkspaceId, "workspace id");
        Require(path, snapshot.DisplayName, "engineering project display name");
        if (snapshot.Snapshots is null)
        {
            throw InvalidResource(path, "configuration snapshots collection is empty");
        }

        var snapshotIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configurationSnapshot in snapshot.Snapshots)
        {
            if (configurationSnapshot is null)
            {
                throw InvalidResource(path, "contains an empty configuration snapshot");
            }

            Require(path, configurationSnapshot.SnapshotId, "configuration snapshot id");
            Require(path, configurationSnapshot.ProjectId, "configuration snapshot project id");
            Require(path, configurationSnapshot.ProcessDefinitionId, "process definition id");
            Require(path, configurationSnapshot.ProcessVersionId, "process version id");
            Require(path, configurationSnapshot.RecipeId, "recipe id");
            Require(path, configurationSnapshot.RecipeVersionId, "recipe version id");
            Require(path, configurationSnapshot.StationProfileId, "station profile id");

            if (!string.Equals(
                    configurationSnapshot.ProjectId,
                    snapshot.ProjectId,
                    StringComparison.Ordinal))
            {
                throw InvalidResource(
                    path,
                    $"configuration snapshot {configurationSnapshot.SnapshotId} belongs to engineering project {configurationSnapshot.ProjectId}, expected {snapshot.ProjectId}");
            }

            if (!string.Equals(configurationSnapshot.Status, "Published", StringComparison.Ordinal))
            {
                throw InvalidResource(
                    path,
                    $"configuration snapshot {configurationSnapshot.SnapshotId} has unsupported status '{configurationSnapshot.Status}'");
            }

            if (!snapshotIds.Add(configurationSnapshot.SnapshotId))
            {
                throw InvalidResource(
                    path,
                    $"configuration snapshot id '{configurationSnapshot.SnapshotId}' is duplicated");
            }

            ValidateDeviceBindingSnapshots(
                path,
                configurationSnapshot.SnapshotId,
                configurationSnapshot.DeviceBindings);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ActiveSnapshotId)
            && !snapshotIds.Contains(snapshot.ActiveSnapshotId))
        {
            throw InvalidResource(
                path,
                $"active configuration snapshot '{snapshot.ActiveSnapshotId}' does not exist");
        }
    }

    private static void ValidateRecipeSnapshot(string path, PersistedRecipe snapshot)
    {
        Require(path, snapshot.RecipeId, "recipe id");
        Require(path, snapshot.VersionId, "recipe version id");
        Require(path, snapshot.DisplayName, "recipe display name");
        Require(path, snapshot.Status, "recipe status");
        if (snapshot.Parameters is null)
        {
            throw InvalidResource(path, "recipe parameters collection is empty");
        }

        if (string.Equals(snapshot.Status, "Published", StringComparison.Ordinal))
        {
            if (snapshot.PublishedAtUtc is null)
            {
                throw InvalidResource(path, "published recipe has no publication timestamp");
            }
        }
        else if (!string.Equals(snapshot.Status, "Draft", StringComparison.Ordinal))
        {
            throw InvalidResource(path, $"recipe status '{snapshot.Status}' is not supported");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in snapshot.Parameters)
        {
            if (parameter is null)
            {
                throw InvalidResource(path, "contains an empty recipe parameter");
            }

            Require(path, parameter.Key, "recipe parameter key");
            Require(path, parameter.Value, "recipe parameter value");
            if (!keys.Add(parameter.Key))
            {
                throw InvalidResource(path, $"recipe parameter key '{parameter.Key}' is duplicated");
            }
        }
    }

    private static void ValidateStationProfileSnapshot(
        string path,
        PersistedStationProfile snapshot)
    {
        Require(path, snapshot.StationProfileId, "station profile id");
        Require(path, snapshot.StationSystemId, "station system id");
        Require(path, snapshot.DisplayName, "station profile display name");
        if (snapshot.DeviceBindings is null)
        {
            throw InvalidResource(path, "device bindings collection is empty");
        }

        ValidateDeviceBindings(path, snapshot.DeviceBindings);
    }

    private static void ValidateDeviceBindings(string path, PersistedDeviceBinding[] bindings)
    {
        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        var capabilityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (binding is null)
            {
                throw InvalidResource(path, "contains an empty device binding");
            }

            ValidateDeviceBinding(
                path,
                binding.DeviceBindingId,
                binding.CapabilityId,
                binding.DeviceKey,
                bindingIds,
                capabilityIds);
        }
    }

    private static void ValidateDeviceBindingSnapshots(
        string path,
        string snapshotId,
        PersistedDeviceBindingSnapshot[]? bindings)
    {
        if (bindings is null)
        {
            throw InvalidResource(
                path,
                $"configuration snapshot {snapshotId} has no device bindings collection");
        }

        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        var capabilityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (binding is null)
            {
                throw InvalidResource(
                    path,
                    $"configuration snapshot {snapshotId} contains an empty device binding");
            }

            ValidateDeviceBinding(
                path,
                binding.DeviceBindingId,
                binding.CapabilityId,
                binding.DeviceKey,
                bindingIds,
                capabilityIds);
        }
    }

    private static void ValidateDeviceBinding(
        string path,
        string bindingId,
        string capabilityId,
        string deviceKey,
        HashSet<string> bindingIds,
        HashSet<string> capabilityIds)
    {
        Require(path, bindingId, "device binding id");
        Require(path, capabilityId, "device capability id");
        Require(path, deviceKey, "device key");
        if (!bindingIds.Add(bindingId))
        {
            throw InvalidResource(path, $"device binding id '{bindingId}' is duplicated");
        }

        if (!capabilityIds.Add(capabilityId))
        {
            throw InvalidResource(path, $"device capability id '{capabilityId}' is duplicated");
        }
    }

    private static void Require(string path, string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidResource(path, $"{field} is empty");
        }
    }

    private static InvalidDataException InvalidResource(string path, string message)
    {
        return new InvalidDataException($"Project engineering resource '{path}' {message}.");
    }
}
