using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Processes.Application.Runtime;

public interface IProjectProcessRuntimeSessionLauncher
{
    ValueTask<Result<StartedProcessRuntimeSessionDetails>> StartAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        StartProcessRuntimeSessionRequest request,
        CancellationToken cancellationToken = default);
}
