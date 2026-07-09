using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Operations;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;
using OpenLineOps.Engineering.Infrastructure.Persistence;
using OpenLineOps.Engineering.Infrastructure.Processes;

namespace OpenLineOps.Engineering.Tests;

public sealed class SqliteEngineeringRepositoryTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(10);

    [Fact]
    public async Task RepositoriesPersistEngineeringConfigurationForNewRepositoryInstances()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var workspaceRepository = new SqliteWorkspaceRepository(database.ConnectionString);
        using var projectRepository = new SqliteEngineeringProjectRepository(database.ConnectionString);
        using var recipeRepository = new SqliteRecipeRepository(database.ConnectionString);
        using var stationProfileRepository = new SqliteStationProfileRepository(database.ConnectionString);
        var workspace = CreateWorkspace();
        var recipe = CreatePublishedRecipe("recipe-eol");
        var stationProfile = CreateStationProfile("station-eol");
        var project = CreateProject("project-packaging");

        AssertAccepted(project.PublishSnapshot(
            SnapshotId("snapshot-001"),
            ProcessDefinitionId("process-packaging"),
            ProcessVersionId("process-packaging@1.0.0"),
            recipe,
            stationProfile,
            PublishedAtUtc));

        await workspaceRepository.SaveAsync(workspace);
        await recipeRepository.SaveAsync(recipe);
        await stationProfileRepository.SaveAsync(stationProfile);
        await projectRepository.SaveAsync(project);

        using var restartedWorkspaceRepository = new SqliteWorkspaceRepository(database.ConnectionString);
        using var restartedProjectRepository = new SqliteEngineeringProjectRepository(database.ConnectionString);
        using var restartedRecipeRepository = new SqliteRecipeRepository(database.ConnectionString);
        using var restartedStationProfileRepository = new SqliteStationProfileRepository(database.ConnectionString);
        var restoredWorkspace = await restartedWorkspaceRepository.GetByIdAsync(workspace.Id);
        var restoredProject = await restartedProjectRepository.GetByIdAsync(project.Id);
        var restoredRecipe = await restartedRecipeRepository.GetByIdAsync(recipe.Id);
        var restoredStationProfile = await restartedStationProfileRepository.GetByIdAsync(stationProfile.Id);

        Assert.NotNull(restoredWorkspace);
        Assert.Equal("workspace-main", restoredWorkspace.Id.Value);
        Assert.Equal("Main Workspace", restoredWorkspace.DisplayName);
        Assert.Equal(CreatedAtUtc, restoredWorkspace.CreatedAtUtc);

        Assert.NotNull(restoredRecipe);
        Assert.Equal(RecipeStatus.Published, restoredRecipe.Status);
        Assert.Equal(PublishedAtUtc, restoredRecipe.PublishedAtUtc);
        Assert.Equal("5.2", Assert.Single(restoredRecipe.Parameters).Value);
        Assert.Empty(restoredRecipe.DomainEvents);

        Assert.NotNull(restoredStationProfile);
        var binding = Assert.Single(restoredStationProfile.DeviceBindings);
        Assert.Equal("device.scanner", binding.CapabilityId.Value);
        Assert.Equal("scanner-01", binding.DeviceKey);

        Assert.NotNull(restoredProject);
        Assert.Equal("snapshot-001", restoredProject.ActiveSnapshotId?.Value);
        Assert.Empty(restoredProject.DomainEvents);
        var snapshot = Assert.Single(restoredProject.Snapshots);
        Assert.Equal(project.Id, snapshot.ProjectId);
        Assert.Equal("process-packaging", snapshot.ProcessDefinitionId.Value);
        Assert.Equal("process-packaging@1.0.0", snapshot.ProcessVersionId.Value);
        Assert.Equal(recipe.Id, snapshot.RecipeId);
        Assert.Equal(recipe.VersionId, snapshot.RecipeVersionId);
        Assert.Equal(stationProfile.Id, snapshot.StationProfileId);
        Assert.Equal(PublishedAtUtc, snapshot.PublishedAtUtc);

        var resolver = new EngineeringRuntimeConfigurationSnapshotResolver(restartedProjectRepository);
        var resolved = await resolver.ResolveAsync("snapshot-001");

        Assert.True(resolved.IsSuccess, resolved.Error.Message);
        Assert.Equal("snapshot-001", resolved.Value.ConfigurationSnapshotId);
        Assert.Equal("process-packaging", resolved.Value.ProcessDefinitionId);
        Assert.Equal("process-packaging@1.0.0", resolved.Value.ProcessVersionId);
        Assert.Equal("recipe-eol@1.0.0", resolved.Value.RecipeSnapshotId);
        Assert.Equal("station-eol", resolved.Value.StationId);
    }

    [Fact]
    public async Task ListAsyncReturnsEngineeringDocumentsOrderedById()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var workspaceRepository = new SqliteWorkspaceRepository(database.ConnectionString);
        using var projectRepository = new SqliteEngineeringProjectRepository(database.ConnectionString);
        using var recipeRepository = new SqliteRecipeRepository(database.ConnectionString);
        using var stationProfileRepository = new SqliteStationProfileRepository(database.ConnectionString);

        await workspaceRepository.SaveAsync(CreateWorkspace("workspace-z"));
        await workspaceRepository.SaveAsync(CreateWorkspace("workspace-a"));
        await projectRepository.SaveAsync(CreateProject("project-z"));
        await projectRepository.SaveAsync(CreateProject("project-a"));
        await recipeRepository.SaveAsync(CreatePublishedRecipe("recipe-z"));
        await recipeRepository.SaveAsync(CreatePublishedRecipe("recipe-a"));
        await stationProfileRepository.SaveAsync(CreateStationProfile("station-z"));
        await stationProfileRepository.SaveAsync(CreateStationProfile("station-a"));

        var workspaces = await workspaceRepository.ListAsync();
        var projects = await projectRepository.ListAsync();
        var recipes = await recipeRepository.ListAsync();
        var stationProfiles = await stationProfileRepository.ListAsync();

        Assert.Collection(
            workspaces,
            workspace => Assert.Equal(new WorkspaceId("workspace-a"), workspace.Id),
            workspace => Assert.Equal(new WorkspaceId("workspace-z"), workspace.Id));
        Assert.Collection(
            projects,
            project => Assert.Equal(new EngineeringProjectId("project-a"), project.Id),
            project => Assert.Equal(new EngineeringProjectId("project-z"), project.Id));
        Assert.Collection(
            recipes,
            recipe => Assert.Equal(new RecipeId("recipe-a"), recipe.Id),
            recipe => Assert.Equal(new RecipeId("recipe-z"), recipe.Id));
        Assert.Collection(
            stationProfiles,
            stationProfile => Assert.Equal(new StationProfileId("station-a"), stationProfile.Id),
            stationProfile => Assert.Equal(new StationProfileId("station-z"), stationProfile.Id));
    }

    [Fact]
    public async Task MissingLookupsReturnNull()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var workspaceRepository = new SqliteWorkspaceRepository(database.ConnectionString);
        using var projectRepository = new SqliteEngineeringProjectRepository(database.ConnectionString);
        using var recipeRepository = new SqliteRecipeRepository(database.ConnectionString);
        using var stationProfileRepository = new SqliteStationProfileRepository(database.ConnectionString);

        var workspace = await workspaceRepository.GetByIdAsync(new WorkspaceId("missing-workspace"));
        var project = await projectRepository.GetByIdAsync(new EngineeringProjectId("missing-project"));
        var recipe = await recipeRepository.GetByIdAsync(new RecipeId("missing-recipe"));
        var stationProfile = await stationProfileRepository.GetByIdAsync(new StationProfileId("missing-station"));

        Assert.Null(workspace);
        Assert.Null(project);
        Assert.Null(recipe);
        Assert.Null(stationProfile);
    }

    private static Workspace CreateWorkspace(string workspaceId = "workspace-main")
    {
        return Workspace.Create(
            WorkspaceId(workspaceId),
            "Main Workspace",
            CreatedAtUtc);
    }

    private static EngineeringProject CreateProject(string projectId)
    {
        return EngineeringProject.Create(
            EngineeringProjectId(projectId),
            WorkspaceId("workspace-main"),
            "Packaging Line Project",
            CreatedAtUtc);
    }

    private static Recipe CreatePublishedRecipe(string recipeId)
    {
        var recipe = Recipe.Create(
            RecipeId(recipeId),
            RecipeVersionId($"{recipeId}@1.0.0"),
            "End Of Line Recipe",
            CreatedAtUtc);

        AssertAccepted(recipe.AddOrUpdateParameter("voltage.max", "5.2"));
        AssertAccepted(recipe.Publish(PublishedAtUtc));
        recipe.ClearDomainEvents();

        return recipe;
    }

    private static StationProfile CreateStationProfile(string stationProfileId)
    {
        var stationProfile = StationProfile.Create(
            StationProfileId(stationProfileId),
            "End Of Line Station");

        AssertAccepted(stationProfile.AddDeviceBinding(DeviceBinding.Create(
            DeviceBindingId("scanner-primary"),
            DeviceCapabilityId("device.scanner"),
            "scanner-01")));

        return stationProfile;
    }

    private static void AssertAccepted(EngineeringOperationResult result)
    {
        Assert.True(result.Succeeded, result.Message);
    }

    private static ConfigurationSnapshotId SnapshotId(string value)
    {
        return new ConfigurationSnapshotId(value);
    }

    private static DeviceBindingId DeviceBindingId(string value)
    {
        return new DeviceBindingId(value);
    }

    private static DeviceCapabilityId DeviceCapabilityId(string value)
    {
        return new DeviceCapabilityId(value);
    }

    private static EngineeringProjectId EngineeringProjectId(string value)
    {
        return new EngineeringProjectId(value);
    }

    private static ProcessDefinitionId ProcessDefinitionId(string value)
    {
        return new ProcessDefinitionId(value);
    }

    private static ProcessVersionId ProcessVersionId(string value)
    {
        return new ProcessVersionId(value);
    }

    private static RecipeId RecipeId(string value)
    {
        return new RecipeId(value);
    }

    private static RecipeVersionId RecipeVersionId(string value)
    {
        return new RecipeVersionId(value);
    }

    private static StationProfileId StationProfileId(string value)
    {
        return new StationProfileId(value);
    }

    private static WorkspaceId WorkspaceId(string value)
    {
        return new WorkspaceId(value);
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        public string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "engineering.sqlite");

            return new TemporarySqliteDatabase(directory, databasePath);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
