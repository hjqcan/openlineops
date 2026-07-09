namespace OpenLineOps.Plugins.Application.Commands;

public interface IPluginDeviceCommandInvoker
{
    ValueTask<PluginDeviceCommandInvocationResult> ExecuteAsync(
        PluginDeviceCommandInvocationRequest request,
        CancellationToken cancellationToken = default);
}
