using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Processes.Application.Runtime;

public interface IProcessRuntimeSessionLauncher
{
    ValueTask<Result<StartedProcessRuntimeSessionDetails>> StartAsync(
        string processDefinitionId,
        StartProcessRuntimeSessionRequest request,
        CancellationToken cancellationToken = default);
}
