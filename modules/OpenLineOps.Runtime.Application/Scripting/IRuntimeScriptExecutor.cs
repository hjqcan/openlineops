using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Runtime.Application.Scripting;

public interface IRuntimeScriptExecutor
{
    ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeScriptExecutionRequest request,
        CancellationToken cancellationToken = default);
}
