namespace OpenLineOps.Plugin.Abstractions;

public interface IOpenLineOpsProcessNodePlugin : IOpenLineOpsPlugin
{
    ValueTask<PluginProcessCommandExecutionResult> ExecuteProcessCommandAsync(
        PluginProcessCommandExecutionRequest request,
        CancellationToken cancellationToken = default);
}
