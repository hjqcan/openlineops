using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public sealed class ConfigurableRuntimeScriptExecutor : IRuntimeScriptExecutor
{
    private readonly PythonScriptRuntimeOptions _options;
    private readonly PythonScriptRuntimeScriptExecutor _inProcessExecutor;
    private readonly ProcessIsolatedPythonScriptRuntimeScriptExecutor _processIsolatedExecutor;

    public ConfigurableRuntimeScriptExecutor(
        PythonScriptRuntimeOptions options,
        PythonScriptRuntimeScriptExecutor inProcessExecutor,
        ProcessIsolatedPythonScriptRuntimeScriptExecutor processIsolatedExecutor)
    {
        _options = options;
        _inProcessExecutor = inProcessExecutor;
        _processIsolatedExecutor = processIsolatedExecutor;
    }

    public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeScriptExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var mode = _options.ExecutionMode;

        if (PythonScriptRuntimeExecutionModes.IsInProcessTrusted(mode))
        {
            return _inProcessExecutor.ExecuteAsync(request, cancellationToken);
        }

        if (PythonScriptRuntimeExecutionModes.IsProcessIsolated(mode))
        {
            return _processIsolatedExecutor.ExecuteAsync(request, cancellationToken);
        }

        return ValueTask.FromResult(RuntimeCommandExecutionResult.Rejected(
            $"Unsupported Python script execution mode '{mode}'."));
    }
}
