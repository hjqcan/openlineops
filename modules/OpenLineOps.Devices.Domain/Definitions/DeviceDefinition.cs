using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Operations;
using OpenLineOps.Domain.Abstractions.Entities;

namespace OpenLineOps.Devices.Domain.Definitions;

public sealed class DeviceDefinition : AggregateRoot<DeviceDefinitionId>
{
    private readonly List<DeviceCapability> _capabilities = [];
    private readonly List<DeviceCommandDefinition> _commands = [];

    private DeviceDefinition(DeviceDefinitionId id, string displayName, string pluginId, DateTimeOffset createdAtUtc)
        : base(id)
    {
        DisplayName = DeviceIdGuard.NotBlank(displayName, nameof(displayName));
        PluginId = DeviceIdGuard.NotBlank(pluginId, nameof(pluginId));
        CreatedAtUtc = createdAtUtc;
    }

    public string DisplayName { get; }

    public string PluginId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyCollection<DeviceCapability> Capabilities => _capabilities.AsReadOnly();

    public IReadOnlyCollection<DeviceCommandDefinition> Commands => _commands.AsReadOnly();

    public static DeviceDefinition Create(
        DeviceDefinitionId id,
        string displayName,
        string pluginId,
        DateTimeOffset createdAtUtc)
    {
        return new DeviceDefinition(id, displayName, pluginId, createdAtUtc);
    }

    public static DeviceDefinition Restore(
        DeviceDefinitionId id,
        string displayName,
        string pluginId,
        DateTimeOffset createdAtUtc,
        IEnumerable<DeviceCapability> capabilities,
        IEnumerable<DeviceCommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(commands);

        var definition = new DeviceDefinition(id, displayName, pluginId, createdAtUtc);

        foreach (var capability in capabilities)
        {
            var result = definition.AddCapability(capability);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Message);
            }
        }

        foreach (var command in commands)
        {
            var result = definition.AddCommand(command);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Message);
            }
        }

        definition.ClearDomainEvents();

        return definition;
    }

    public DeviceOperationResult AddCapability(DeviceCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (_capabilities.Any(candidate => candidate.Id == capability.Id))
        {
            return DeviceOperationResult.Rejected(
                "Devices.CapabilityAlreadyExists",
                $"Capability {capability.Id} already exists in device definition {Id}.");
        }

        _capabilities.Add(capability);

        return DeviceOperationResult.Accepted("Capability added.");
    }

    public DeviceOperationResult AddCommand(DeviceCommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_commands.Any(candidate => candidate.Id == command.Id))
        {
            return DeviceOperationResult.Rejected(
                "Devices.CommandAlreadyExists",
                $"Command definition {command.Id} already exists in device definition {Id}.");
        }

        if (_commands.Any(candidate => string.Equals(
            candidate.CommandName,
            command.CommandName,
            StringComparison.OrdinalIgnoreCase)))
        {
            return DeviceOperationResult.Rejected(
                "Devices.CommandNameAlreadyExists",
                $"Command name {command.CommandName} already exists in device definition {Id}.");
        }

        if (_capabilities.All(candidate => candidate.Id != command.CapabilityId))
        {
            return DeviceOperationResult.Rejected(
                "Devices.CommandCapabilityMissing",
                $"Capability {command.CapabilityId} must be declared before command {command.Id} can be added.");
        }

        _commands.Add(command);

        return DeviceOperationResult.Accepted("Command definition added.");
    }
}
