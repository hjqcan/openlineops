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
        IEnumerable<OperationInputMappingPlan> inputMappings,
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
        ArgumentNullException.ThrowIfNull(inputMappings);
        var mappings = inputMappings.ToArray();
        if (mappings.Any(static mapping => mapping is null)
            || mappings.Select(static mapping => mapping.TargetInputKey)
                .Distinct(StringComparer.Ordinal).Count() != mappings.Length
            || mappings.Select(static mapping => mapping.TargetInputKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != mappings.Length
            || mappings.Any(mapping => string.Equals(
                mapping.SourceOperationId,
                Definition.OperationId,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Operation input mappings require unique target keys and a different source Operation.",
                nameof(inputMappings));
        }

        InputMappings = mappings
            .OrderBy(static mapping => mapping.TargetInputKey, StringComparer.Ordinal)
            .ToArray();
    }

    public OperationRunDefinition Definition { get; }

    public ExecutableRuntimeProcess FrozenExecutableProcess { get; }

    public IReadOnlyList<OperationInputMappingPlan> InputMappings { get; }
}
