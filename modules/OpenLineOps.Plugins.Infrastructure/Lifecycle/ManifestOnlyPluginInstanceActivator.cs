using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Lifecycle;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ManifestOnlyPluginInstanceActivator : IPluginInstanceActivator
{
    public ValueTask<PluginActivationResult> ActivateAsync(
        PluginPackageDescriptor package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            PluginActivationResult.Success(new ManifestOnlyOpenLineOpsPlugin(package.Manifest)));
    }

    private sealed class ManifestOnlyOpenLineOpsPlugin(PluginManifest manifest) : IOpenLineOpsPlugin
    {
        public PluginManifest Manifest { get; } = manifest;

        public ValueTask<PluginInitializationStatus> InitializeAsync(
            IServiceProvider services,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(services);
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(PluginInitializationStatus.Initialized);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
