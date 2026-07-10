using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Processes.Application.Runtime;

public interface IProjectRuntimeConfigurationSnapshotResolver
{
    ValueTask<Result<RuntimeConfigurationSnapshotDetails>> ResolveAsync(
        ProjectApplicationWorkspaceScope scope,
        string configurationSnapshotId,
        CancellationToken cancellationToken = default);
}
