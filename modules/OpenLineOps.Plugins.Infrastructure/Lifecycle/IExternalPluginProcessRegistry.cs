using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public interface IExternalPluginProcessRegistry
{
    void Register(string pluginId, IExternalPluginProcess process);

    void Register(PluginPackageRuntimeIdentity identity, IExternalPluginProcess process);

    bool TryGet(string pluginId, out IExternalPluginProcess process);

    bool TryGet(PluginPackageRuntimeIdentity identity, out IExternalPluginProcess process);

    void Unregister(string pluginId, IExternalPluginProcess process);

    void Unregister(PluginPackageRuntimeIdentity identity, IExternalPluginProcess process);
}
