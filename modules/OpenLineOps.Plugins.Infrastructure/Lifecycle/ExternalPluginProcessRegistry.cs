using System.Collections.Concurrent;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalPluginProcessRegistry : IExternalPluginProcessRegistry
{
    private readonly ConcurrentDictionary<string, IExternalPluginProcess> _processes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<PluginPackageRuntimeIdentity, IExternalPluginProcess> _exactProcesses = new();

    public void Register(string pluginId, IExternalPluginProcess process)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        }

        ArgumentNullException.ThrowIfNull(process);

        _processes[pluginId.Trim()] = process;
    }

    public void Register(PluginPackageRuntimeIdentity identity, IExternalPluginProcess process)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(process);
        if (!identity.IsComplete)
        {
            throw new ArgumentException("Plugin package runtime identity must be complete.", nameof(identity));
        }

        _exactProcesses[identity] = process;
        Register(identity.PluginId, process);
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

    public bool TryGet(PluginPackageRuntimeIdentity identity, out IExternalPluginProcess process)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.IsComplete)
        {
            process = null!;
            return false;
        }

        return _exactProcesses.TryGetValue(identity, out process!);
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

    public void Unregister(PluginPackageRuntimeIdentity identity, IExternalPluginProcess process)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(process);
        var exactEntry = new KeyValuePair<PluginPackageRuntimeIdentity, IExternalPluginProcess>(identity, process);
        ((ICollection<KeyValuePair<PluginPackageRuntimeIdentity, IExternalPluginProcess>>)_exactProcesses)
            .Remove(exactEntry);
        Unregister(identity.PluginId, process);
    }
}
