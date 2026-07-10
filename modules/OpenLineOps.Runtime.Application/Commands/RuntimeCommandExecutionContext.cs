using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Commands;

public sealed record RuntimeCommandExecutionContext
{
    public RuntimeCommandExecutionContext(
        RuntimeSessionId sessionId,
        ProductionRunId productionRunId,
        string productionLineDefinitionId,
        string productionStageId,
        int stageSequence,
        string workstationId,
        DutIdentity dutIdentity,
        StationId stationId,
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
        string projectSnapshotId)
    {
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("sessionId cannot be empty.", nameof(sessionId))
            : sessionId;
        ProductionRunId = productionRunId.Value == Guid.Empty
            ? throw new ArgumentException("productionRunId cannot be empty.", nameof(productionRunId))
            : productionRunId;
        ProductionLineDefinitionId = Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        ProductionStageId = Required(productionStageId, nameof(productionStageId));
        if (stageSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stageSequence), "Stage sequence must be positive.");
        }

        StageSequence = stageSequence;
        WorkstationId = Required(workstationId, nameof(workstationId));
        DutIdentity = dutIdentity ?? throw new ArgumentNullException(nameof(dutIdentity));
        StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
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
        Timeout = timeout;
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        TargetKind = Required(targetKind, nameof(targetKind));
        TargetId = Required(targetId, nameof(targetId));
        ProjectId = Required(projectId, nameof(projectId));
        ApplicationId = Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = Required(projectSnapshotId, nameof(projectSnapshotId));
    }

    public RuntimeSessionId SessionId { get; }

    public ProductionRunId ProductionRunId { get; }

    public string ProductionLineDefinitionId { get; }

    public string ProductionStageId { get; }

    public int StageSequence { get; }

    public string WorkstationId { get; }

    public DutIdentity DutIdentity { get; }

    public StationId StationId { get; }

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
