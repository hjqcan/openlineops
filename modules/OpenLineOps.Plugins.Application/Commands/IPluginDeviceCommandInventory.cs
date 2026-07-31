using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Application.Commands;

public interface IPluginDeviceCommandInventory
{
    ValueTask<IReadOnlyCollection<PluginDeviceCommandDescriptor>> ListDeviceCommandsAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    async ValueTask<PluginDeviceCommandDescriptor?> FindDeviceCommandAsync(
        string capability,
        string commandName,
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        var commands = await ListDeviceCommandsAsync(scope, cancellationToken).ConfigureAwait(false);

        return commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Capability, capability, StringComparison.Ordinal)
            && string.Equals(candidate.CommandName, commandName, StringComparison.Ordinal));
    }
}
