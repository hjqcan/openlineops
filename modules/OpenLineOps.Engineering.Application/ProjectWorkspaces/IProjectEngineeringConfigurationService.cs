using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Engineering.Application.Configuration;

namespace OpenLineOps.Engineering.Application.ProjectWorkspaces;

public interface IProjectEngineeringConfigurationService
{
    Task<Result<WorkspaceDetails>> CreateWorkspaceAsync(
        string projectId,
        string applicationId,
        CreateWorkspaceRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<WorkspaceDetails>> GetWorkspaceAsync(
        string projectId,
        string applicationId,
        string workspaceId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<WorkspaceDetails>>> ListWorkspacesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringProjectDetails>> CreateProjectAsync(
        string projectId,
        string applicationId,
        CreateEngineeringProjectRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringProjectDetails>> GetProjectAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<EngineeringProjectDetails>>> ListProjectsAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<Result<RecipeDetails>> CreateRecipeAsync(
        string projectId,
        string applicationId,
        CreateRecipeRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<RecipeDetails>> GetRecipeAsync(
        string projectId,
        string applicationId,
        string recipeId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<RecipeDetails>>> ListRecipesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<Result<RecipeDetails>> PublishRecipeAsync(
        string projectId,
        string applicationId,
        string recipeId,
        CancellationToken cancellationToken = default);

    Task<Result<StationProfileDetails>> CreateStationProfileAsync(
        string projectId,
        string applicationId,
        CreateStationProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<StationProfileDetails>> GetStationProfileAsync(
        string projectId,
        string applicationId,
        string stationProfileId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<StationProfileDetails>>> ListStationProfilesAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringProjectDetails>> PublishSnapshotAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        PublishConfigurationSnapshotRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringProjectDetails>> RollbackSnapshotAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        string snapshotId,
        CancellationToken cancellationToken = default);

    Task<Result<ConfigurationSnapshotDiffDetails>> CompareSnapshotsAsync(
        string projectId,
        string applicationId,
        string engineeringProjectId,
        string fromSnapshotId,
        string toSnapshotId,
        CancellationToken cancellationToken = default);
}
