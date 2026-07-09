namespace OpenLineOps.Plugins.Application.Discovery;

public interface IPluginPackageCatalog
{
    ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default);
}
