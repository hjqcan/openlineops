using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Application.Discovery;

public interface IPluginPackageCatalog
{
    ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);
}
