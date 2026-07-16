using System.Collections.Concurrent;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalPluginProcessRegistry : IExternalPluginProcessRegistry
{
    private readonly ConcurrentDictionary<PluginPackageExecutionIdentity, IExternalPluginProcess> _processes = [];

    public void Register(PluginPackageExecutionIdentity identity, IExternalPluginProcess process)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(process);
        if (!_processes.TryAdd(identity, process))
        {
            throw new InvalidOperationException(
                $"Plugin process '{identity.PluginId}' is already active for Project '{identity.ProjectId}', Application '{identity.ApplicationId}', and package SHA-256 '{identity.PackageIdentity.PackageContentSha256}'.");
        }
    }

    public bool TryGet(
        PluginPackageExecutionIdentity identity,
        out IExternalPluginProcess process)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _processes.TryGetValue(identity, out process!);
    }

    public void Unregister(
        PluginPackageExecutionIdentity identity,
        IExternalPluginProcess process)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(process);
        var entry = new KeyValuePair<PluginPackageExecutionIdentity, IExternalPluginProcess>(
            identity,
            process);
        ((ICollection<KeyValuePair<PluginPackageExecutionIdentity, IExternalPluginProcess>>)_processes)
            .Remove(entry);
    }
}
