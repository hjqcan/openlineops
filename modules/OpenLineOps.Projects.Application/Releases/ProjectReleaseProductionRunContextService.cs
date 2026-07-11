using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Application.Projects;

namespace OpenLineOps.Projects.Application.Releases;

public sealed class ProjectReleaseProductionRunContextService(
    IAutomationProjectService projectService,
    IProjectReleaseSnapshotReader releaseReader) : IProjectReleaseProductionRunContextService
{
    public async ValueTask<Result<ProjectReleaseProductionRunContext>> GetAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (!IsCanonical(projectId) || !IsCanonical(snapshotId))
        {
            return Failure(ApplicationError.Validation(
                "Projects.ProjectSnapshotIdentityInvalid",
                "ProjectId and SnapshotId must be canonical non-empty text."));
        }

        var projectResult = await projectService
            .GetByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (projectResult.IsFailure)
        {
            return Failure(projectResult.Error);
        }

        var snapshots = projectResult.Value.Snapshots
            .Where(candidate => string.Equals(candidate.SnapshotId, snapshotId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (snapshots.Length == 0)
        {
            return Failure(ApplicationError.NotFound(
                "Projects.ProjectSnapshotNotFound",
                $"Project snapshot {snapshotId} was not found in automation project {projectId}."));
        }

        if (snapshots.Length != 1)
        {
            return Failure(ApplicationError.Conflict(
                "Projects.ProjectSnapshotIdentityConflict",
                $"Project snapshot {snapshotId} is duplicated in automation project {projectId}."));
        }

        var verifiedResult = await releaseReader
            .OpenAsync(snapshots[0], cancellationToken)
            .ConfigureAwait(false);
        if (verifiedResult.IsFailure)
        {
            return Failure(verifiedResult.Error);
        }

        var line = verifiedResult.Value.Artifact.Metadata.ProductionLine;
        var entryOperations = line.Operations
            .Where(operation => string.Equals(
                operation.OperationId,
                line.EntryOperationId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (entryOperations.Length != 1)
        {
            return Failure(ApplicationError.Conflict(
                "Projects.ProjectReleaseEntryOperationInvalid",
                $"Immutable release {snapshotId} must contain exactly one entry Operation {line.EntryOperationId}."));
        }

        var entryOperation = entryOperations[0];
        return Result.Success(new ProjectReleaseProductionRunContext(
            verifiedResult.Value.Artifact.ProjectId,
            verifiedResult.Value.Artifact.ApplicationId,
            verifiedResult.Value.Artifact.SnapshotId,
            verifiedResult.Value.Artifact.Metadata.TopologyId,
            line.LineDefinitionId,
            line.ProductModel.ProductModelId,
            line.ProductModel.IdentityInputKey,
            line.EntryOperationId,
            entryOperation.StationSystemId,
            ProjectReleaseStationDeploymentSet.Resolve(line)));
    }

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static Result<ProjectReleaseProductionRunContext> Failure(ApplicationError error) =>
        Result.Failure<ProjectReleaseProductionRunContext>(error);
}
