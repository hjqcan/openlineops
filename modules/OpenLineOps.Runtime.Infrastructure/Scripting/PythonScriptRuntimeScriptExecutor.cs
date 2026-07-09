using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public sealed class PythonScriptRuntimeScriptExecutor : IRuntimeScriptExecutor
{
    public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeScriptExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var result = PythonScriptExecutionScope.Execute(
            PythonScriptExecutionScopeRequest.FromRuntimeRequest(request),
            cancellationToken);

        return ValueTask.FromResult(result);
    }
}
