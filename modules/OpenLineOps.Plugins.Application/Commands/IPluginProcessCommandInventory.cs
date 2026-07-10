namespace OpenLineOps.Plugins.Application.Commands;

public interface IPluginProcessCommandInventory
{
    ValueTask<IReadOnlyCollection<PluginProcessCommandDescriptor>> ListProcessCommandsAsync(
        CancellationToken cancellationToken = default);

    async ValueTask<PluginProcessCommandDescriptor?> FindProcessCommandAsync(
        string capability,
        string commandName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        var commands = await ListProcessCommandsAsync(cancellationToken).ConfigureAwait(false);

        return commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Capability, capability, StringComparison.Ordinal)
            && string.Equals(candidate.CommandName, commandName, StringComparison.Ordinal));
    }
}
