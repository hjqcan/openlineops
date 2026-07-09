namespace OpenLineOps.Plugins.Application.Lifecycle;

public interface IPluginLifecycleManager
{
    ValueTask<IReadOnlyCollection<PluginLifecycleRecord>> StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<PluginLifecycleRecord>> StopAsync(
        CancellationToken cancellationToken = default);
}
