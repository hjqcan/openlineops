using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Application.Lifecycle;

public interface IPluginInstanceActivator
{
    ValueTask<PluginActivationResult> ActivateAsync(
        PluginPackageDescriptor package,
        CancellationToken cancellationToken = default);
}
