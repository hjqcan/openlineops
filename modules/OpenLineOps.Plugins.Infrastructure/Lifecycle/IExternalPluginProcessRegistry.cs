using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public interface IExternalPluginProcessRegistry
{
    void Register(PluginPackageExecutionIdentity identity, IExternalPluginProcess process);

    bool TryGet(PluginPackageExecutionIdentity identity, out IExternalPluginProcess process);

    void Unregister(PluginPackageExecutionIdentity identity, IExternalPluginProcess process);
}
