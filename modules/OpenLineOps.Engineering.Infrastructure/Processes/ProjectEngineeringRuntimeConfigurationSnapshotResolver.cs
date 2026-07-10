using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Processes.Application.Runtime;

namespace OpenLineOps.Engineering.Infrastructure.Processes;

public sealed class ProjectEngineeringRuntimeConfigurationSnapshotResolver :
    IProjectRuntimeConfigurationSnapshotResolver
{
    private readonly IProjectEngineeringConfigurationRepository _repository;

    public ProjectEngineeringRuntimeConfigurationSnapshotResolver(
        IProjectEngineeringConfigurationRepository repository)
    {
        _repository = repository;
    }

    public async ValueTask<Result<RuntimeConfigurationSnapshotDetails>> ResolveAsync(
        ProjectApplicationWorkspaceScope scope,
        string configurationSnapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(configurationSnapshotId))
        {
            return Result.Failure<RuntimeConfigurationSnapshotDetails>(ApplicationError.Validation(
                "Engineering.ConfigurationSnapshotIdRequired",
                "ConfigurationSnapshotId is required."));
        }

        var projects = await _repository
            .ListProjectsAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        var matchingSnapshots = projects
            .SelectMany(project => project.Snapshots)
            .Where(candidate => string.Equals(
                candidate.Id.Value,
                configurationSnapshotId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();

        if (matchingSnapshots.Length == 0)
        {
            return Result.Failure<RuntimeConfigurationSnapshotDetails>(ApplicationError.NotFound(
                "Engineering.ConfigurationSnapshotNotFound",
                $"Configuration snapshot {configurationSnapshotId} was not found in application {scope.ApplicationId}."));
        }

        if (matchingSnapshots.Length > 1)
        {
            return Result.Failure<RuntimeConfigurationSnapshotDetails>(ApplicationError.Conflict(
                "Engineering.ConfigurationSnapshotIdAmbiguous",
                $"Configuration snapshot {configurationSnapshotId} is not unique in application {scope.ApplicationId}."));
        }

        var snapshot = matchingSnapshots[0];
        if (!snapshot.IsPublished)
        {
            return Result.Failure<RuntimeConfigurationSnapshotDetails>(ApplicationError.Conflict(
                "Engineering.ConfigurationSnapshotNotPublished",
                $"Configuration snapshot {configurationSnapshotId} must be published before runtime can start."));
        }

        return Result.Success(new RuntimeConfigurationSnapshotDetails(
            snapshot.Id.Value,
            snapshot.ProcessDefinitionId.Value,
            snapshot.ProcessVersionId.Value,
            snapshot.RecipeVersionId.Value,
            snapshot.StationProfileId.Value));
    }
}
