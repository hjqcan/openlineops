using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Application.Capabilities;

public sealed class PluginCapabilityInventory : IPluginCapabilityInventory
{
    private readonly IPluginPackageCatalog _packageCatalog;
    private readonly IPluginManifestValidator _manifestValidator;

    public PluginCapabilityInventory(
        IPluginPackageCatalog packageCatalog,
        IPluginManifestValidator manifestValidator)
    {
        _packageCatalog = packageCatalog;
        _manifestValidator = manifestValidator;
    }

    public async ValueTask<IReadOnlyCollection<PluginCapabilityDescriptor>> ListCapabilitiesAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        var packages = await _packageCatalog
            .DiscoverAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        var capabilities = new Dictionary<string, PluginCapabilityDescriptor>(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var validationReport = _manifestValidator.Validate(package.Manifest);
            if (!validationReport.IsValid)
            {
                continue;
            }

            foreach (var capability in package.Manifest.Capabilities)
            {
                if (string.IsNullOrWhiteSpace(capability))
                {
                    continue;
                }

                capabilities.TryAdd(
                    capability,
                    new PluginCapabilityDescriptor(
                        package.Manifest.Id,
                        package.Manifest.Name,
                        package.Manifest.Kind,
                        capability));
            }
        }

        return capabilities.Values
            .OrderBy(capability => capability.Capability, StringComparer.Ordinal)
            .ThenBy(capability => capability.PluginId, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<bool> HasCapabilityAsync(
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
