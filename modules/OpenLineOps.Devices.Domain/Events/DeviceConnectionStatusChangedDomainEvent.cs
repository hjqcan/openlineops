using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;
using OpenLineOps.Domain.Abstractions.Events;

namespace OpenLineOps.Devices.Domain.Events;

public sealed record DeviceConnectionStatusChangedDomainEvent(
    DeviceInstanceId DeviceInstanceId,
    DeviceConnectionStatus PreviousStatus,
    DeviceConnectionStatus NewStatus,
    string? Reason) : DomainEvent("Devices.DeviceConnectionStatusChanged");
