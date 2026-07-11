using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Application.Projects;

namespace OpenLineOps.Projects.Application.Releases;

public interface IProjectReleaseSnapshotReader
{
    ValueTask<Result<VerifiedProjectRelease>> OpenAsync(
        PublishedProjectSnapshotDetails snapshot,
        CancellationToken cancellationToken = default);
}

public sealed record VerifiedProjectRelease(
    PublishedProjectSnapshotDetails Snapshot,
    ProjectApplicationWorkspaceScope ReleaseScope,
    OpenedProjectReleaseArtifact Artifact);
