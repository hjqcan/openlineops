using OpenLineOps.Plugins.Application.Commands;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalProcessPluginProcessCommandInvoker : IPluginProcessCommandInvoker
{
    private readonly IExternalPluginProcessRegistry _processRegistry;

    public ExternalProcessPluginProcessCommandInvoker(IExternalPluginProcessRegistry processRegistry)
    {
        _processRegistry = processRegistry;
    }

    public async ValueTask<PluginProcessCommandInvocationResult> ExecuteAsync(
        PluginProcessCommandInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_processRegistry.TryGet(request.PluginId, out var process))
        {
            return PluginProcessCommandInvocationResult.Rejected(
                $"External plugin process '{request.PluginId}' is not running.");
        }

        if (process.HasExited)
        {
            return PluginProcessCommandInvocationResult.Failed(
                $"External plugin process '{request.PluginId}' has exited.");
        }

        return await process
            .ExecuteProcessCommandAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
