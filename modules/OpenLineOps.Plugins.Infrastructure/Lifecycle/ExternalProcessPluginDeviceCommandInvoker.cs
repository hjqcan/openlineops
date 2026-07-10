using OpenLineOps.Plugins.Application.Commands;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalProcessPluginDeviceCommandInvoker : IPluginDeviceCommandInvoker
{
    private readonly IExternalPluginProcessRegistry _processRegistry;

    public ExternalProcessPluginDeviceCommandInvoker(IExternalPluginProcessRegistry processRegistry)
    {
        _processRegistry = processRegistry;
    }

    public async ValueTask<PluginDeviceCommandInvocationResult> ExecuteAsync(
        PluginDeviceCommandInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var found = request.PackageIdentity is { IsComplete: true }
            ? _processRegistry.TryGet(request.PackageIdentity, out var process)
            : _processRegistry.TryGet(request.PluginId, out process);
        if (!found)
        {
            return PluginDeviceCommandInvocationResult.Rejected(
                request.PackageIdentity is null
                    ? $"External plugin process '{request.PluginId}' is not running."
                    : $"Exact external plugin package '{request.PluginId}' version {request.PackageIdentity.Version} with content SHA-256 {request.PackageIdentity.PackageContentSha256} is not running.");
        }

        if (process.HasExited)
        {
            return PluginDeviceCommandInvocationResult.Failed(
                $"External plugin process '{request.PluginId}' has exited.");
        }

        return await process
            .ExecuteDeviceCommandAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
