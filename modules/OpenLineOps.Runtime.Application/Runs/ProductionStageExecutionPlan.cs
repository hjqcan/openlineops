using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Runs;

public sealed record ProductionStageExecutionPlan
{
    public ProductionStageExecutionPlan(
        string productionLineDefinitionId,
        string stageId,
        int sequence,
        string workstationId,
        StationId stationId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        ExecutableRuntimeProcess executableProcess)
    {
        ProductionLineDefinitionId = Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        StageId = Required(stageId, nameof(stageId));
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Stage sequence must be positive.");
        }

        Sequence = sequence;
        WorkstationId = Required(workstationId, nameof(workstationId));
        StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
        ConfigurationSnapshotId = configurationSnapshotId
            ?? throw new ArgumentNullException(nameof(configurationSnapshotId));
        RecipeSnapshotId = recipeSnapshotId ?? throw new ArgumentNullException(nameof(recipeSnapshotId));
        ArgumentNullException.ThrowIfNull(executableProcess);
        FrozenExecutableProcess = new ExecutableRuntimeProcess(
            executableProcess.ProcessDefinitionId,
            executableProcess.ProcessVersionId,
            executableProcess.Nodes.ToArray())
        {
            StartNodeId = executableProcess.StartNodeId,
            RoutingNodes = executableProcess.RoutingNodes.ToArray(),
            Transitions = executableProcess.Transitions.ToArray()
        };
    }

    public string ProductionLineDefinitionId { get; }

    public string StageId { get; }

    public int Sequence { get; }

    public string WorkstationId { get; }

    public StationId StationId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public RecipeSnapshotId RecipeSnapshotId { get; }

    public ExecutableRuntimeProcess FrozenExecutableProcess { get; }

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
