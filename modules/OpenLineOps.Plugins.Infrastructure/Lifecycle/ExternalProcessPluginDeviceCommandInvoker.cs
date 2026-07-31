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

        if (!_processRegistry.TryGet(request.PackageIdentity, out var process))
        {
            return PluginDeviceCommandInvocationResult.Rejected(
                $"Exact external plugin package '{request.PluginId}' for Project '{request.PackageIdentity.ProjectId}', Application '{request.PackageIdentity.ApplicationId}', version {request.PackageIdentity.PackageIdentity.Version}, and content SHA-256 {request.PackageIdentity.PackageIdentity.PackageContentSha256} is not running.");
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
