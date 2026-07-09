using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Application.Execution;

public sealed record DeviceCommandRoute(
    DeviceInstanceId DeviceInstanceId,
    DeviceCommandDefinitionId CommandDefinitionId,
    DeviceCapabilityId CapabilityId);
