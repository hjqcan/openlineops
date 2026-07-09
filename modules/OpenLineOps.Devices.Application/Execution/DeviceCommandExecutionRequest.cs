using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Application.Execution;

public sealed record DeviceCommandExecutionRequest(
    DeviceInstanceId DeviceInstanceId,
    DeviceCommandDefinitionId CommandDefinitionId,
    DeviceCapabilityId CapabilityId,
    string CommandName,
    string? InputPayload,
    TimeSpan Timeout);
