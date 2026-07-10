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

        var useActive = string.IsNullOrWhiteSpace(requestedSnapshot)
            || string.Equals(requestedSnapshot.Trim(), "active", StringComparison.OrdinalIgnoreCase);
        var snapshotId = useActive ? project.ActiveSnapshotId : requestedSnapshot!.Trim();
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
