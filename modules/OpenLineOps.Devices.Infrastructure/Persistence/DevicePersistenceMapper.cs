using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;

namespace OpenLineOps.Devices.Infrastructure.Persistence;

internal static class DevicePersistenceMapper
{
    public static PersistedDeviceDefinition ToSnapshot(DeviceDefinition definition)
    {
        return new PersistedDeviceDefinition(
            definition.Id.Value,
            definition.DisplayName,
            definition.PluginId,
            definition.CreatedAtUtc,
            definition.Capabilities.Select(ToSnapshot).ToArray(),
            definition.Commands.Select(ToSnapshot).ToArray());
    }

    public static PersistedDeviceInstance ToSnapshot(DeviceInstance instance)
    {
        return new PersistedDeviceInstance(
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

    public static DeviceDefinition ToAggregate(PersistedDeviceDefinition snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return DeviceDefinition.Restore(
            new DeviceDefinitionId(snapshot.DefinitionId),
            snapshot.DisplayName,
            snapshot.PluginId,
            snapshot.CreatedAtUtc,
            snapshot.Capabilities.Select(ToAggregate),
            snapshot.Commands.Select(ToAggregate));
    }

    public static DeviceInstance ToAggregate(PersistedDeviceInstance snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return DeviceInstance.Restore(
            new DeviceInstanceId(snapshot.InstanceId),
            new DeviceDefinitionId(snapshot.DefinitionId),
            snapshot.StationId,
            snapshot.DisplayName,
            new DeviceEndpoint(snapshot.EndpointProtocol, snapshot.EndpointAddress),
            snapshot.RegisteredAtUtc,
            ParseEnum<DeviceConnectionStatus>(snapshot.Status, nameof(snapshot.Status)),
            snapshot.ConnectedAtUtc,
            snapshot.LastDisconnectedAtUtc,
            snapshot.FaultReason);
    }

    private static PersistedDeviceCapability ToSnapshot(DeviceCapability capability)
    {
        return new PersistedDeviceCapability(
            capability.Id.Value,
            capability.DisplayName);
    }

    private static PersistedDeviceCommandDefinition ToSnapshot(DeviceCommandDefinition command)
    {
        return new PersistedDeviceCommandDefinition(
            command.Id.Value,
            command.CapabilityId.Value,
            command.CommandName,
            command.InputSchema,
            command.OutputSchema,
            command.Timeout,
            command.MaxRetries);
    }

    private static DeviceCapability ToAggregate(PersistedDeviceCapability capability)
    {
        return DeviceCapability.Create(
            new DeviceCapabilityId(capability.CapabilityId),
            capability.DisplayName);
    }

    private static DeviceCommandDefinition ToAggregate(PersistedDeviceCommandDefinition command)
    {
        return DeviceCommandDefinition.Create(
            new DeviceCommandDefinitionId(command.CommandDefinitionId),
            new DeviceCapabilityId(command.CapabilityId),
            command.CommandName,
            command.InputSchema,
            command.OutputSchema,
            command.Timeout,
            command.MaxRetries);
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Persisted {fieldName} value '{value}' is invalid.");
    }
}

internal sealed record PersistedDeviceDefinition(
    string DefinitionId,
    string DisplayName,
    string PluginId,
    DateTimeOffset CreatedAtUtc,
    PersistedDeviceCapability[] Capabilities,
    PersistedDeviceCommandDefinition[] Commands);

internal sealed record PersistedDeviceCapability(
    string CapabilityId,
    string DisplayName);

internal sealed record PersistedDeviceCommandDefinition(
    string CommandDefinitionId,
    string CapabilityId,
    string CommandName,
    string? InputSchema,
    string? OutputSchema,
    TimeSpan Timeout,
    int MaxRetries);

internal sealed record PersistedDeviceInstance(
    string InstanceId,
    string DefinitionId,
    string StationId,
    string DisplayName,
    string EndpointProtocol,
    string EndpointAddress,
    DateTimeOffset RegisteredAtUtc,
    string Status,
    DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? LastDisconnectedAtUtc,
    string? FaultReason);
