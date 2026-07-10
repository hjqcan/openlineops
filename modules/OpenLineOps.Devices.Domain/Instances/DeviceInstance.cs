using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Operations;
using OpenLineOps.Domain.Abstractions.Entities;

namespace OpenLineOps.Devices.Domain.Instances;

public sealed class DeviceInstance : AggregateRoot<DeviceInstanceId>
{
    private DeviceInstance()
        : base(new DeviceInstanceId("__ef_materialization__"))
    {
        DefinitionId = new DeviceDefinitionId("__ef_materialization__");
        StationId = "__ef_materialization__";
        DisplayName = "__ef_materialization__";
        Endpoint = new DeviceEndpoint("__ef_materialization__", "__ef_materialization__");
    }

    private DeviceInstance(
        DeviceInstanceId id,
        DeviceDefinitionId definitionId,
        string stationId,
        string displayName,
        DeviceEndpoint endpoint,
        DateTimeOffset registeredAtUtc)
        : base(id)
    {
        DefinitionId = definitionId;
        StationId = DeviceIdGuard.NotBlank(stationId, nameof(stationId));
        DisplayName = DeviceIdGuard.NotBlank(displayName, nameof(displayName));
        Endpoint = endpoint;
        RegisteredAtUtc = registeredAtUtc;
        Status = DeviceConnectionStatus.Disconnected;
    }

    public DeviceDefinitionId DefinitionId { get; private set; }

    public string StationId { get; private set; }

    public string DisplayName { get; private set; }

    public DeviceEndpoint Endpoint { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private set; }

    public DeviceConnectionStatus Status { get; private set; }

    public DateTimeOffset? ConnectedAtUtc { get; private set; }

    public DateTimeOffset? LastDisconnectedAtUtc { get; private set; }

    public string? FaultReason { get; private set; }

    public static DeviceInstance Register(
        DeviceInstanceId id,
        DeviceDefinitionId definitionId,
        string stationId,
        string displayName,
        DeviceEndpoint endpoint,
        DateTimeOffset registeredAtUtc)
    {
        return new DeviceInstance(id, definitionId, stationId, displayName, endpoint, registeredAtUtc);
    }

    public static DeviceInstance Restore(
        DeviceInstanceId id,
        DeviceDefinitionId definitionId,
        string stationId,
        string displayName,
        DeviceEndpoint endpoint,
        DateTimeOffset registeredAtUtc,
        DeviceConnectionStatus status,
        DateTimeOffset? connectedAtUtc,
        DateTimeOffset? lastDisconnectedAtUtc,
        string? faultReason)
    {
        var instance = new DeviceInstance(id, definitionId, stationId, displayName, endpoint, registeredAtUtc)
        {
            Status = status,
            ConnectedAtUtc = connectedAtUtc,
            LastDisconnectedAtUtc = lastDisconnectedAtUtc,
            FaultReason = string.IsNullOrWhiteSpace(faultReason) ? null : faultReason.Trim()
        };
        instance.ClearDomainEvents();

        return instance;
    }

    public DeviceOperationResult RequestConnection()
    {
        if (Status == DeviceConnectionStatus.Connected || Status == DeviceConnectionStatus.Connecting)
        {
            return DeviceOperationResult.Accepted("Connection already requested.");
        }

        if (Status == DeviceConnectionStatus.Faulted)
        {
            return DeviceOperationResult.Rejected(
                "Devices.DeviceFaulted",
                $"Device {Id} is faulted and must be reset before reconnecting.");
        }

        ChangeStatus(DeviceConnectionStatus.Connecting);

        return DeviceOperationResult.Accepted("Connection requested.");
    }

    public DeviceOperationResult ConfirmConnected(DateTimeOffset connectedAtUtc)
    {
        if (Status == DeviceConnectionStatus.Connected)
        {
            return DeviceOperationResult.Accepted("Device already connected.");
        }

        if (Status != DeviceConnectionStatus.Connecting)
        {
            return DeviceOperationResult.Rejected(
                "Devices.ConnectionNotRequested",
                $"Device {Id} cannot be marked connected from {Status}.");
        }

        ConnectedAtUtc = connectedAtUtc;
        FaultReason = null;
        ChangeStatus(DeviceConnectionStatus.Connected);

        return DeviceOperationResult.Accepted("Device connected.");
    }

    public DeviceOperationResult Disconnect(DateTimeOffset disconnectedAtUtc, string reason)
    {
        if (Status == DeviceConnectionStatus.Disconnected)
        {
            return DeviceOperationResult.Accepted("Device already disconnected.");
        }

        LastDisconnectedAtUtc = disconnectedAtUtc;
        ConnectedAtUtc = null;
        FaultReason = null;
        ChangeStatus(DeviceConnectionStatus.Disconnected);

        return DeviceOperationResult.Accepted("Device disconnected.");
    }

    public DeviceOperationResult MarkFaulted(string reason)
    {
        var normalizedReason = DeviceIdGuard.NotBlank(reason, nameof(reason));

        ConnectedAtUtc = null;
        FaultReason = normalizedReason;
        ChangeStatus(DeviceConnectionStatus.Faulted);

        return DeviceOperationResult.Accepted("Device faulted.");
    }

    public DeviceOperationResult ResetFault()
    {
        if (Status != DeviceConnectionStatus.Faulted)
        {
            return DeviceOperationResult.Accepted("Device is not faulted.");
        }

        FaultReason = null;
        ChangeStatus(DeviceConnectionStatus.Disconnected);

        return DeviceOperationResult.Accepted("Fault reset.");
    }

    private void ChangeStatus(DeviceConnectionStatus newStatus)
    {
        if (Status == newStatus)
        {
            return;
        }

        Status = newStatus;
    }
}
