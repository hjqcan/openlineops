namespace OpenLineOps.Plugins.Application.Capabilities;

public interface IPluginCapabilityInventory
{
    ValueTask<IReadOnlyCollection<PluginCapabilityDescriptor>> ListCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    async ValueTask<bool> HasCapabilityAsync(
        string capability,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        var capabilities = await ListCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

        return capabilities.Any(candidate => string.Equals(
            candidate.Capability,
            capability.Trim(),
            StringComparison.Ordinal));
    }
}
