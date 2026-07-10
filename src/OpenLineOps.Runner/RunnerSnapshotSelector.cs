using OpenLineOps.Projects.Application.Projects;

namespace OpenLineOps.Runner;

public sealed record RunnerSnapshotSelection(
    PublishedProjectSnapshotDetails? Snapshot,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsSuccess => Snapshot is not null;
}

public static class RunnerSnapshotSelector
{
    public static RunnerSnapshotSelection Select(
        AutomationProjectDetails project,
        string? requestedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (requestedSnapshot is not null
            && (string.IsNullOrWhiteSpace(requestedSnapshot)
                || char.IsWhiteSpace(requestedSnapshot[0])
                || char.IsWhiteSpace(requestedSnapshot[^1])))
        {
            return Failure(
                "Runner.SnapshotSelectionInvalid",
                "Snapshot selection must be null or a non-empty canonical value.");
        }

        var useActive = requestedSnapshot is null
            || string.Equals(requestedSnapshot, "active", StringComparison.Ordinal);
        var snapshotId = useActive ? project.ActiveSnapshotId : requestedSnapshot;
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return Failure(
                "Runner.ActiveSnapshotMissing",
                $"Project {project.ProjectId} has no active published snapshot.");
        }

        var snapshot = project.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, snapshotId, StringComparison.Ordinal));
        if (snapshot is null)
        {
            return Failure(
                "Runner.SnapshotNotFound",
                useActive
                    ? $"Active snapshot {snapshotId} was not found in project {project.ProjectId}."
                    : $"Snapshot {snapshotId} was not found in project {project.ProjectId}.");
        }

        return new RunnerSnapshotSelection(snapshot, ErrorCode: null, ErrorMessage: null);
    }

    private static RunnerSnapshotSelection Failure(string code, string message)
    {
        return new RunnerSnapshotSelection(Snapshot: null, code, message);
    }
}
