using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Processes.Application.Runtime;

namespace OpenLineOps.Engineering.Infrastructure.Processes;

public sealed class EngineeringRuntimeConfigurationSnapshotResolver : IRuntimeConfigurationSnapshotResolver
{
    private readonly IEngineeringProjectRepository _projectRepository;

    public EngineeringRuntimeConfigurationSnapshotResolver(IEngineeringProjectRepository projectRepository)
    {
        _projectRepository = projectRepository;
    }

    public async ValueTask<Result<RuntimeConfigurationSnapshotDetails>> ResolveAsync(
        string configurationSnapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configurationSnapshotId))
        {
            return Result.Failure<RuntimeConfigurationSnapshotDetails>(ApplicationError.Validation(
                "Engineering.ConfigurationSnapshotIdRequired",
                "ConfigurationSnapshotId is required."));
        }

        var projects = await _projectRepository.ListAsync(cancellationToken).ConfigureAwait(false);
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
                $"Configuration snapshot {configurationSnapshotId} was not found."));
        }

        if (matchingSnapshots.Length > 1)
        {
            return Result.Failure<RuntimeConfigurationSnapshotDetails>(ApplicationError.Conflict(
                "Engineering.ConfigurationSnapshotIdAmbiguous",
                $"Configuration snapshot {configurationSnapshotId} is not unique across engineering projects."));
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
