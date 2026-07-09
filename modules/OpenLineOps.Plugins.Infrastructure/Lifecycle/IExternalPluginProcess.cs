using OpenLineOps.Plugins.Application.Commands;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public interface IExternalPluginProcess : IAsyncDisposable
{
    bool HasExited { get; }

    ValueTask<PluginDeviceCommandInvocationResult> ExecuteDeviceCommandAsync(
        PluginDeviceCommandInvocationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PluginProcessCommandInvocationResult> ExecuteProcessCommandAsync(
        PluginProcessCommandInvocationRequest request,
        CancellationToken cancellationToken = default);
}
