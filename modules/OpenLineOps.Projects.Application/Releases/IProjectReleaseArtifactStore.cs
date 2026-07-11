using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Projects.Application.Releases;

public interface IProjectReleaseArtifactStore
{
    ValueTask<ProjectReleaseArtifactDescriptor> PublishAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        DateTimeOffset publishedAtUtc,
        ProjectReleaseSourceMetadata metadata,
        CancellationToken cancellationToken = default);

    ValueTask<OpenedProjectReleaseArtifact?> OpenAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        string expectedContentSha256,
        CancellationToken cancellationToken = default);

    ValueTask RollbackPublicationAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        string expectedContentSha256,
        CancellationToken cancellationToken = default);
}
