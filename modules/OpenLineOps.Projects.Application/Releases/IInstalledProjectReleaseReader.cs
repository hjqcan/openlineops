namespace OpenLineOps.Projects.Application.Releases;

public interface IInstalledProjectReleaseReader
{
    ValueTask<OpenedProjectReleaseArtifact> OpenAsync(
        string releaseRootPath,
        string expectedProjectId,
        string expectedApplicationId,
        string expectedSnapshotId,
        CancellationToken cancellationToken = default);
}
