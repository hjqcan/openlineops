using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Application.Lifecycle;

public interface IPluginLifecycleManager
{
    ValueTask<IReadOnlyCollection<PluginLifecycleRecord>> StartAsync(
        ProjectApplicationWorkspaceScope scope,
        IServiceProvider services,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<PluginLifecycleRecord>> StopAsync(
        CancellationToken cancellationToken = default);
}
