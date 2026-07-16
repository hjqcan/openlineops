using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Application.Lifecycle;

public interface IPluginInstanceActivator
{
    ValueTask<PluginActivationResult> ActivateAsync(
        ProjectApplicationWorkspaceScope scope,
        PluginPackageDescriptor package,
        CancellationToken cancellationToken = default);
}
