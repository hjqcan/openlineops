using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Projects.Application.ExternalPrograms;

public interface IExternalProgramTrialExecutor
{
    ValueTask<Result<ExternalProgramProtocolTrialResult>> ExecuteAsync(
        ProjectApplicationWorkspaceScope scope,
        ExternalProgramResource resource,
        ExternalProgramProtocolTrialRequest request,
        CancellationToken cancellationToken = default);
}

public interface IExternalProgramResourceUsageInspector
{
    ValueTask<bool> IsReferencedAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken = default);
}
