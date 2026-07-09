using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Plugins.Application.Capabilities;
using OpenLineOps.Plugins.Application.Commands;
using DeviceCapabilityId = OpenLineOps.Devices.Domain.Identifiers.DeviceCapabilityId;
using DeviceCommandDefinitionId = OpenLineOps.Devices.Domain.Identifiers.DeviceCommandDefinitionId;
using DeviceInstanceId = OpenLineOps.Devices.Domain.Identifiers.DeviceInstanceId;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class EngineeringConfigurationDeviceCommandRouteResolver : IDeviceCommandRouteResolver
{
    private readonly IEngineeringProjectRepository _projectRepository;
    private readonly IPluginCapabilityInventory? _capabilityInventory;
    private readonly IPluginDeviceCommandInventory? _commandInventory;

    public EngineeringConfigurationDeviceCommandRouteResolver(
        IEngineeringProjectRepository projectRepository,
        IPluginCapabilityInventory? capabilityInventory = null,
        IPluginDeviceCommandInventory? commandInventory = null)
    {
        _projectRepository = projectRepository;
        _capabilityInventory = capabilityInventory;
        _commandInventory = commandInventory;
    }

    public async ValueTask<DeviceCommandRoute?> ResolveAsync(
        DeviceCommandRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projects = await _projectRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        var matchingSnapshots = projects
            .SelectMany(project => project.Snapshots)
            .Where(snapshot => string.Equals(
                snapshot.Id.Value,
                request.ConfigurationSnapshotId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();

        if (matchingSnapshots.Length != 1)
        {
            return null;
        }

        var snapshot = matchingSnapshots[0];
        if (!snapshot.IsPublished)
        {
            return null;
        }

        if (!string.Equals(snapshot.StationProfileId.Value, request.StationId, StringComparison.Ordinal))
        {
            return null;
        }

        var matchingBindings = snapshot.DeviceBindings
            .Where(binding => string.Equals(
                binding.CapabilityId.Value,
                request.CapabilityId.Value,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();

        if (matchingBindings.Length != 1)
        {
            return null;
        }

        var binding = matchingBindings[0];
        if (_commandInventory is not null)
        {
            var command = await _commandInventory
                .FindDeviceCommandAsync(binding.CapabilityId.Value, request.CommandName, cancellationToken)
                .ConfigureAwait(false);

            if (command is null)
            {
                return null;
            }

            return new DeviceCommandRoute(
                new DeviceInstanceId(binding.DeviceKey),
                new DeviceCommandDefinitionId(command.CommandDefinitionId),
                new DeviceCapabilityId(binding.CapabilityId.Value));
        }

        if (_capabilityInventory is not null)
        {
            var capabilityIsDeclared = await _capabilityInventory
                .HasCapabilityAsync(binding.CapabilityId.Value, cancellationToken)
                .ConfigureAwait(false);

            if (!capabilityIsDeclared)
            {
                return null;
            }
        }

        return new DeviceCommandRoute(
            new DeviceInstanceId(binding.DeviceKey),
            new DeviceCommandDefinitionId($"{binding.CapabilityId.Value}:{NormalizeCommandName(request.CommandName)}"),
            new DeviceCapabilityId(binding.CapabilityId.Value));
    }

    private static string NormalizeCommandName(string commandName)
    {
        return commandName.Replace(" ", "-", StringComparison.Ordinal);
    }
}
