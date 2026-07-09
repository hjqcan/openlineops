namespace OpenLineOps.Plugins.Application.Commands;

public interface IPluginDeviceCommandInventory
{
    ValueTask<IReadOnlyCollection<PluginDeviceCommandDescriptor>> ListDeviceCommandsAsync(
        CancellationToken cancellationToken = default);

    async ValueTask<PluginDeviceCommandDescriptor?> FindDeviceCommandAsync(
        string capability,
        string commandName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        var commands = await ListDeviceCommandsAsync(cancellationToken).ConfigureAwait(false);

        return commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Capability, capability.Trim(), StringComparison.Ordinal)
            && string.Equals(candidate.CommandName, commandName.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
