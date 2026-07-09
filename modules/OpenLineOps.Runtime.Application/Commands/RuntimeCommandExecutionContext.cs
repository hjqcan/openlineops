using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Commands;

public sealed record RuntimeCommandExecutionContext(
    RuntimeSessionId SessionId,
    StationId StationId,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    RuntimeStepId StepId,
    RuntimeCommandId CommandId,
    RuntimeNodeId NodeId,
    RuntimeCapabilityId TargetCapability,
    string CommandName,
    string? InputPayload,
    TimeSpan Timeout);
