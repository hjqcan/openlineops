using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public sealed record OperationExecutionPlan
{
    public OperationExecutionPlan(
        string operationId,
        string stationSystemId,
        StationId stationId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        ExecutableRuntimeProcess executableProcess,
        IEnumerable<ResourceRequirement>? resourceRequirements = null,
        MaterialSlotRequirement? materialSlotRequirement = null)
    {
        ArgumentNullException.ThrowIfNull(executableProcess);
        Definition = new OperationRunDefinition(
            operationId,
            stationSystemId,
            stationId,
            executableProcess.ProcessDefinitionId,
            executableProcess.ProcessVersionId,
            configurationSnapshotId,
            recipeSnapshotId,
            resourceRequirements,
            materialSlotRequirement);
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

    public OperationRunDefinition Definition { get; }

    public ExecutableRuntimeProcess FrozenExecutableProcess { get; }
}
