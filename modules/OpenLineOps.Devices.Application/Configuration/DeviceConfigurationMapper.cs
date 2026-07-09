using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Instances;

namespace OpenLineOps.Devices.Application.Configuration;

public static class DeviceConfigurationMapper
{
    public static DeviceDefinitionDetails ToDetails(DeviceDefinition definition)
    {
        return new DeviceDefinitionDetails(
            definition.Id.Value,
            definition.DisplayName,
            definition.PluginId,
            definition.CreatedAtUtc,
            definition.Capabilities
                .OrderBy(capability => capability.Id.Value, StringComparer.Ordinal)
                .Select(ToDetails)
                .ToArray(),
            definition.Commands
                .OrderBy(command => command.Id.Value, StringComparer.Ordinal)
                .Select(ToDetails)
                .ToArray());
    }

    public static DeviceInstanceDetails ToDetails(DeviceInstance instance)
    {
        return new DeviceInstanceDetails(
            instance.Id.Value,
            instance.DefinitionId.Value,
            instance.StationId,
            instance.DisplayName,
            instance.Endpoint.Protocol,
            instance.Endpoint.Address,
            instance.RegisteredAtUtc,
            instance.Status.ToString(),
            instance.ConnectedAtUtc,
            instance.LastDisconnectedAtUtc,
            instance.FaultReason);
    }

    private static DeviceCapabilityDetails ToDetails(DeviceCapability capability)
    {
        return new DeviceCapabilityDetails(
            capability.Id.Value,
            capability.DisplayName);
    }

    private static DeviceCommandDefinitionDetails ToDetails(DeviceCommandDefinition command)
    {
        return new DeviceCommandDefinitionDetails(
            command.Id.Value,
            command.CapabilityId.Value,
            command.CommandName,
            command.InputSchema,
            command.OutputSchema,
            (int)command.Timeout.TotalSeconds,
            command.MaxRetries);
    }
}
