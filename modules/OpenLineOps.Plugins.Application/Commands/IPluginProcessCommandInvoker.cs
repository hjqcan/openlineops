namespace OpenLineOps.Plugins.Application.Commands;

public interface IPluginProcessCommandInvoker
{
    ValueTask<PluginProcessCommandInvocationResult> ExecuteAsync(
        PluginProcessCommandInvocationRequest request,
        CancellationToken cancellationToken = default);
}
