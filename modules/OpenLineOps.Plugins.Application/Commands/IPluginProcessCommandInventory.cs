using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Application.Commands;

public interface IPluginProcessCommandInventory
{
    ValueTask<IReadOnlyCollection<PluginProcessCommandDescriptor>> ListProcessCommandsAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    async ValueTask<PluginProcessCommandDescriptor?> FindProcessCommandAsync(
        string capability,
        string commandName,
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        var commands = await ListProcessCommandsAsync(scope, cancellationToken).ConfigureAwait(false);

        return commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Capability, capability, StringComparison.Ordinal)
            && string.Equals(candidate.CommandName, commandName, StringComparison.Ordinal));
    }
}
