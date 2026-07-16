using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Application.Commands;

public sealed class PluginProcessCommandInventory : IPluginProcessCommandInventory
{
    private readonly IPluginPackageCatalog _packageCatalog;
    private readonly IPluginManifestValidator _manifestValidator;

    public PluginProcessCommandInventory(
        IPluginPackageCatalog packageCatalog,
        IPluginManifestValidator manifestValidator)
    {
        _packageCatalog = packageCatalog;
        _manifestValidator = manifestValidator;
    }

    public async ValueTask<IReadOnlyCollection<PluginProcessCommandDescriptor>> ListProcessCommandsAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        var packages = await _packageCatalog
            .DiscoverAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        var commands = new Dictionary<string, PluginProcessCommandDescriptor>(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var validationReport = _manifestValidator.Validate(package.Manifest);
            if (!validationReport.IsValid || package.Manifest.ProcessCommands is null)
            {
                continue;
            }

            foreach (var command in package.Manifest.ProcessCommands)
            {
                if (!IsUsable(command))
                {
                    continue;
                }

                commands.TryAdd(
                    CreateCommandKey(command.Capability, command.CommandName),
                    new PluginProcessCommandDescriptor(
                        package.Manifest.Id,
                        package.Manifest.Name,
                        package.Manifest.Kind,
                        command.Id,
                        command.Capability,
                        command.CommandName,
                        command.InputSchema,
                        command.OutputSchema,
                        command.TimeoutMilliseconds,
                        command.MaxRetries,
                        package.RuntimeIdentity));
            }
        }

        return commands.Values
            .OrderBy(command => command.Capability, StringComparer.Ordinal)
            .ThenBy(command => command.CommandName, StringComparer.Ordinal)
            .ThenBy(command => command.PluginId, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<PluginProcessCommandDescriptor?> FindProcessCommandAsync(
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

    private static bool IsUsable(PluginProcessCommandDefinition command)
    {
        return !string.IsNullOrWhiteSpace(command.Id)
            && !string.IsNullOrWhiteSpace(command.Capability)
            && !string.IsNullOrWhiteSpace(command.CommandName)
            && command.TimeoutMilliseconds > 0
            && command.MaxRetries >= 0;
    }

    private static string CreateCommandKey(string capability, string commandName)
    {
        return $"{capability}\u001F{commandName}";
    }
}
