using System.Collections.Concurrent;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalPluginProcessRegistry : IExternalPluginProcessRegistry
{
    private readonly ConcurrentDictionary<string, IExternalPluginProcess> _processes = new(StringComparer.Ordinal);

    public void Register(string pluginId, IExternalPluginProcess process)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        }

        ArgumentNullException.ThrowIfNull(process);

        _processes[pluginId.Trim()] = process;
    }

    public bool TryGet(string pluginId, out IExternalPluginProcess process)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            process = null!;

            return false;
        }

        return _processes.TryGetValue(pluginId.Trim(), out process!);
    }

    public void Unregister(string pluginId, IExternalPluginProcess process)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(process);

        var entry = new KeyValuePair<string, IExternalPluginProcess>(pluginId.Trim(), process);
        ((ICollection<KeyValuePair<string, IExternalPluginProcess>>)_processes).Remove(entry);
    }
}
