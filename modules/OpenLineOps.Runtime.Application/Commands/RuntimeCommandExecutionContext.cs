using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Commands;

public sealed record RuntimeCommandExecutionContext
{
    public RuntimeCommandExecutionContext(
        RuntimeSessionId sessionId,
        ProductionRunId productionRunId,
        ProductionUnitId productionUnitId,
        string productionLineDefinitionId,
        string operationId,
        string operationRunId,
        int operationAttempt,
        string stationSystemId,
        ProductionUnitIdentity productionUnitIdentity,
        string? lotId,
        string? carrierId,
        string? fixtureId,
        string? deviceId,
        ConfigurationSnapshotId configurationSnapshotId,
        RuntimeStepId stepId,
        RuntimeCommandId commandId,
        RuntimeNodeId nodeId,
        RuntimeCapabilityId targetCapability,
        string commandName,
        string? inputPayload,
        TimeSpan timeout,
        RuntimeActionId actionId,
        string targetKind,
        string targetId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        IEnumerable<ResourceLeaseFenceEvidence> resourceLeaseFences)
    {
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("sessionId cannot be empty.", nameof(sessionId))
            : sessionId;
        ProductionRunId = productionRunId.Value == Guid.Empty
            ? throw new ArgumentException("productionRunId cannot be empty.", nameof(productionRunId))
            : productionRunId;
        ProductionUnitId = productionUnitId.Value == Guid.Empty
            ? throw new ArgumentException("productionUnitId cannot be empty.", nameof(productionUnitId))
            : productionUnitId;
        ProductionLineDefinitionId = Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        OperationId = Required(operationId, nameof(operationId));
        OperationRunId = Required(operationRunId, nameof(operationRunId));
        if (operationAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationAttempt),
                "Operation attempt must be positive.");
        }

        OperationAttempt = operationAttempt;
        if (!string.Equals(
                OperationRunId,
                $"{OperationId}@{OperationAttempt:D4}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Operation Run id must match the Operation identity and attempt.",
                nameof(operationRunId));
        }

        StationSystemId = Required(stationSystemId, nameof(stationSystemId));
        ProductionUnitIdentity = productionUnitIdentity
            ?? throw new ArgumentNullException(nameof(productionUnitIdentity));
        LotId = Optional(lotId, nameof(lotId));
        CarrierId = Optional(carrierId, nameof(carrierId));
        FixtureId = Optional(fixtureId, nameof(fixtureId));
        DeviceId = Optional(deviceId, nameof(deviceId));
        ConfigurationSnapshotId = configurationSnapshotId
            ?? throw new ArgumentNullException(nameof(configurationSnapshotId));
        StepId = stepId.Value == Guid.Empty
            ? throw new ArgumentException("stepId cannot be empty.", nameof(stepId))
            : stepId;
        CommandId = commandId.Value == Guid.Empty
            ? throw new ArgumentException("commandId cannot be empty.", nameof(commandId))
            : commandId;
        NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        TargetCapability = targetCapability ?? throw new ArgumentNullException(nameof(targetCapability));
        CommandName = Required(commandName, nameof(commandName));
        InputPayload = inputPayload;
        Timeout = timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout));
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        TargetKind = Required(targetKind, nameof(targetKind));
        TargetId = Required(targetId, nameof(targetId));
        ProjectId = Required(projectId, nameof(projectId));
        ApplicationId = Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = Required(projectSnapshotId, nameof(projectSnapshotId));
        ArgumentNullException.ThrowIfNull(resourceLeaseFences);
        var fences = resourceLeaseFences.ToArray();
        if (fences.Length == 0
            || fences.Any(static fence => fence is null)
            || fences.Select(static fence => fence.Resource).Distinct().Count() != fences.Length
            || !fences.Any(fence => fence.Resource.Kind == ResourceKind.Station
                && string.Equals(
                    fence.Resource.ResourceId,
                    StationSystemId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Runtime command requires unique resource lease fences including its Station.",
                nameof(resourceLeaseFences));
        }

        ResourceLeaseFences = fences
            .OrderBy(static fence => fence.Resource.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
    }

    public RuntimeSessionId SessionId { get; }

    public ProductionRunId ProductionRunId { get; }

    public ProductionUnitId ProductionUnitId { get; }

    public string ProductionLineDefinitionId { get; }

    public string OperationId { get; }

    public string OperationRunId { get; }

    public int OperationAttempt { get; }

    public string StationSystemId { get; }

    public ProductionUnitIdentity ProductionUnitIdentity { get; }

    public string? LotId { get; }

    public string? CarrierId { get; }

    public string? FixtureId { get; }

    public string? DeviceId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public RuntimeStepId StepId { get; }

    public RuntimeCommandId CommandId { get; }

    public RuntimeNodeId NodeId { get; }

    public RuntimeCapabilityId TargetCapability { get; }

    public string CommandName { get; }

    public string? InputPayload { get; }

    public TimeSpan Timeout { get; }

    public RuntimeActionId ActionId { get; }

    public string TargetKind { get; }

    public string TargetId { get; }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public IReadOnlyList<ResourceLeaseFenceEvidence> ResourceLeaseFences { get; }

    private static string? Optional(string? value, string parameterName)
    {
        return value is null ? null : Required(value, parameterName);
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;
    }
}
