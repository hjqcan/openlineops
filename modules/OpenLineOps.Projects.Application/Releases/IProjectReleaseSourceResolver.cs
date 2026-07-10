using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Projects.Application.Releases;

public interface IProjectReleaseSourceResolver
{
    Task<Result<ProjectReleaseSourceMetadata>> ResolveAsync(
        ProjectApplicationWorkspaceScope scope,
        string topologyId,
        string processDefinitionId,
        string configurationSnapshotId,
        CancellationToken cancellationToken = default);
}
