using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Operations;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;
using OpenLineOps.Engineering.Infrastructure.Persistence;
using OpenLineOps.Engineering.Infrastructure.Processes;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresEngineeringRepositoryIntegrationTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(10);

    private readonly PostgresContainerFixture _postgres;

    public PostgresEngineeringRepositoryIntegrationTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [PostgresIntegrationFact]
    public async Task RepositoriesPersistEngineeringConfigurationAndResolveRuntimeSnapshot()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workspace = CreateWorkspace($"workspace-postgres-{suffix}");
        var recipe = CreatePublishedRecipe($"recipe-postgres-{suffix}");
        var stationProfile = CreateStationProfile($"station-postgres-{suffix}");
        var project = CreateProject($"project-postgres-{suffix}", workspace.Id.Value);
        var snapshotId = $"snapshot-postgres-{suffix}";

        AssertAccepted(project.PublishSnapshot(
            new ConfigurationSnapshotId(snapshotId),
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId("process-packaging@1.0.0"),
            recipe,
            stationProfile,
            PublishedAtUtc));

        await using (var workspaceRepository = new PostgresWorkspaceRepository(_postgres.ConnectionString))
        await using (var projectRepository = new PostgresEngineeringProjectRepository(_postgres.ConnectionString))
        await using (var recipeRepository = new PostgresRecipeRepository(_postgres.ConnectionString))
        await using (var stationProfileRepository = new PostgresStationProfileRepository(_postgres.ConnectionString))
        {
            await workspaceRepository.SaveAsync(workspace);
            await recipeRepository.SaveAsync(recipe);
            await stationProfileRepository.SaveAsync(stationProfile);
            await projectRepository.SaveAsync(project);
        }

        await using var restartedWorkspaceRepository = new PostgresWorkspaceRepository(_postgres.ConnectionString);
        await using var restartedProjectRepository = new PostgresEngineeringProjectRepository(_postgres.ConnectionString);
        await using var restartedRecipeRepository = new PostgresRecipeRepository(_postgres.ConnectionString);
        await using var restartedStationProfileRepository = new PostgresStationProfileRepository(_postgres.ConnectionString);

        var restoredWorkspace = await restartedWorkspaceRepository.GetByIdAsync(workspace.Id);
        var restoredProject = await restartedProjectRepository.GetByIdAsync(project.Id);
        var restoredRecipe = await restartedRecipeRepository.GetByIdAsync(recipe.Id);
        var restoredStationProfile = await restartedStationProfileRepository.GetByIdAsync(stationProfile.Id);
        var resolved = await new EngineeringRuntimeConfigurationSnapshotResolver(restartedProjectRepository)
            .ResolveAsync(snapshotId);

        Assert.NotNull(restoredWorkspace);
        Assert.Equal(workspace.Id, restoredWorkspace.Id);
        Assert.Equal(workspace.DisplayName, restoredWorkspace.DisplayName);

        Assert.NotNull(restoredRecipe);
        Assert.Equal(RecipeStatus.Published, restoredRecipe.Status);
        Assert.Equal(PublishedAtUtc, restoredRecipe.PublishedAtUtc);

        Assert.NotNull(restoredStationProfile);
        Assert.Equal("device.scanner", Assert.Single(restoredStationProfile.DeviceBindings).CapabilityId.Value);

        Assert.NotNull(restoredProject);
        Assert.Equal(snapshotId, restoredProject.ActiveSnapshotId?.Value);
        Assert.Empty(restoredProject.DomainEvents);

        Assert.True(resolved.IsSuccess, resolved.Error.Message);
        Assert.Equal(snapshotId, resolved.Value.ConfigurationSnapshotId);
        Assert.Equal("process-packaging", resolved.Value.ProcessDefinitionId);
        Assert.Equal("process-packaging@1.0.0", resolved.Value.ProcessVersionId);
        Assert.Equal($"{recipe.Id.Value}@1.0.0", resolved.Value.RecipeSnapshotId);
        Assert.Equal(stationProfile.Id.Value, resolved.Value.StationId);
    }

    private static Workspace CreateWorkspace(string workspaceId)
    {
        return Workspace.Create(
            new WorkspaceId(workspaceId),
            "Main Workspace",
            CreatedAtUtc);
    }

    private static EngineeringProject CreateProject(string projectId, string workspaceId)
    {
        return EngineeringProject.Create(
            new EngineeringProjectId(projectId),
            new WorkspaceId(workspaceId),
            "Packaging Line Project",
            CreatedAtUtc);
    }

    private static Recipe CreatePublishedRecipe(string recipeId)
    {
        var recipe = Recipe.Create(
            new RecipeId(recipeId),
            new RecipeVersionId($"{recipeId}@1.0.0"),
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
            new StationProfileId(stationProfileId),
            "End Of Line Station");

        AssertAccepted(stationProfile.AddDeviceBinding(DeviceBinding.Create(
            new DeviceBindingId($"scanner-primary-{Guid.NewGuid():N}"),
            new DeviceCapabilityId("device.scanner"),
            "scanner-01")));

        return stationProfile;
    }

    private static void AssertAccepted(EngineeringOperationResult result)
    {
        Assert.True(result.Succeeded, result.Message);
    }
}
