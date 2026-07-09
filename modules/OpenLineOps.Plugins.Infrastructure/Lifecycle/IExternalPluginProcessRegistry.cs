namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public interface IExternalPluginProcessRegistry
{
    void Register(string pluginId, IExternalPluginProcess process);

    bool TryGet(string pluginId, out IExternalPluginProcess process);

    void Unregister(string pluginId, IExternalPluginProcess process);
}
