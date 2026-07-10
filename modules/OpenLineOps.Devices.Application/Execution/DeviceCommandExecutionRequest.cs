using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Application.Execution;

public sealed record DeviceCommandExecutionRequest
{
    public DeviceCommandExecutionRequest(
        string providerKind,
        string providerKey,
        DeviceInstanceId deviceInstanceId,
        DeviceCommandDefinitionId commandDefinitionId,
        DeviceCapabilityId capabilityId,
        string commandName,
        string? inputPayload,
        TimeSpan timeout,
        DevicePluginPackageIdentity? pluginPackage = null)
    {
        ProviderKind = Required(providerKind, nameof(providerKind));
        ProviderKey = Required(providerKey, nameof(providerKey));
        DeviceInstanceId = deviceInstanceId ?? throw new ArgumentNullException(nameof(deviceInstanceId));
        CommandDefinitionId = commandDefinitionId ?? throw new ArgumentNullException(nameof(commandDefinitionId));
        CapabilityId = capabilityId ?? throw new ArgumentNullException(nameof(capabilityId));
        CommandName = Required(commandName, nameof(commandName));
        InputPayload = inputPayload;
        Timeout = timeout;
        PluginPackage = pluginPackage;

        if (string.Equals(
                ProviderKind,
                ProjectReleaseRuntimeProviderKinds.PluginCommand,
                StringComparison.Ordinal)
            && PluginPackage is null)
        {
            throw new ArgumentException(
                "PluginCommand release routes must declare a frozen plugin package identity.",
                nameof(pluginPackage));
        }

        if (!string.Equals(
                ProviderKind,
                ProjectReleaseRuntimeProviderKinds.PluginCommand,
                StringComparison.Ordinal)
            && PluginPackage is not null)
        {
            throw new ArgumentException(
                "Only PluginCommand release routes may declare a plugin package identity.",
                nameof(pluginPackage));
        }
    }

    public string ProviderKind { get; }

    public string ProviderKey { get; }

    public DeviceInstanceId DeviceInstanceId { get; }

    public DeviceCommandDefinitionId CommandDefinitionId { get; }

    public DeviceCapabilityId CapabilityId { get; }

    public string CommandName { get; }

    public string? InputPayload { get; }

    public TimeSpan Timeout { get; }

    public DevicePluginPackageIdentity? PluginPackage { get; }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} cannot be empty.", parameterName)
            : value.Trim();
    }
}
