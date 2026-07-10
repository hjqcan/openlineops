using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Application.Persistence;

public interface IProjectEngineeringConfigurationRepository
{
    Task SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        Workspace workspace,
        CancellationToken cancellationToken = default);

    Task<Workspace?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Workspace>> ListWorkspacesAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        EngineeringProject project,
        CancellationToken cancellationToken = default);

    Task<EngineeringProject?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        EngineeringProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<EngineeringProject>> ListProjectsAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        Recipe recipe,
        CancellationToken cancellationToken = default);

    Task<Recipe?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        RecipeId recipeId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Recipe>> ListRecipesAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        StationProfile stationProfile,
        CancellationToken cancellationToken = default);

    Task<StationProfile?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        StationProfileId stationProfileId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<StationProfile>> ListStationProfilesAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);
}
