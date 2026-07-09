namespace OpenLineOps.Plugin.Abstractions;

public interface IOpenLineOpsDeviceCommandPlugin : IOpenLineOpsPlugin
{
    ValueTask<PluginDeviceCommandExecutionResult> ExecuteDeviceCommandAsync(
        PluginDeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default);
}
