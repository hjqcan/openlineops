using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Engineering.Application.Configuration;

public interface IEngineeringConfigurationService
{
    Task<Result<WorkspaceDetails>> CreateWorkspaceAsync(
        CreateWorkspaceRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<WorkspaceDetails>> GetWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<WorkspaceDetails>>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringProjectDetails>> CreateProjectAsync(
        CreateEngineeringProjectRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringProjectDetails>> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<EngineeringProjectDetails>>> ListProjectsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<RecipeDetails>> CreateRecipeAsync(
        CreateRecipeRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<RecipeDetails>> GetRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<RecipeDetails>>> ListRecipesAsync(
        CancellationToken cancellationToken = default);

    Task<Result<RecipeDetails>> PublishRecipeAsync(
        string recipeId,
        CancellationToken cancellationToken = default);

    Task<Result<StationProfileDetails>> CreateStationProfileAsync(
        CreateStationProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<StationProfileDetails>> GetStationProfileAsync(
        string stationProfileId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<StationProfileDetails>>> ListStationProfilesAsync(
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringProjectDetails>> PublishSnapshotAsync(
        string projectId,
        PublishConfigurationSnapshotRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringProjectDetails>> RollbackSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default);

    Task<Result<ConfigurationSnapshotDiffDetails>> CompareSnapshotsAsync(
        string projectId,
        string fromSnapshotId,
        string toSnapshotId,
        CancellationToken cancellationToken = default);
}
