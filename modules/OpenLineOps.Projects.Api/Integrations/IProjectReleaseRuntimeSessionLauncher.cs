using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Projects.Application.Projects;

namespace OpenLineOps.Projects.Api.Integrations;

public interface IProjectReleaseRuntimeSessionLauncher
{
    ValueTask<Result<StartedProcessRuntimeSessionDetails>> StartAsync(
        PublishedProjectSnapshotDetails snapshot,
        StartProcessRuntimeSessionRequest request,
        CancellationToken cancellationToken = default);
}
