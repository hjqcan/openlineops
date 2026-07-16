using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Application.Capabilities;

public interface IPluginCapabilityInventory
{
    ValueTask<IReadOnlyCollection<PluginCapabilityDescriptor>> ListCapabilitiesAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    async ValueTask<bool> HasCapabilityAsync(
        string capability,
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        var capabilities = await ListCapabilitiesAsync(scope, cancellationToken).ConfigureAwait(false);

        return capabilities.Any(candidate => string.Equals(
            candidate.Capability,
            capability,
            StringComparison.Ordinal));
    }
}
