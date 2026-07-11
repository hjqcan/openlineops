using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed record OperationRunDefinition
{
    public OperationRunDefinition(
        string operationId,
        string stationSystemId,
        StationId stationId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        IEnumerable<ResourceRequirement>? resourceRequirements = null,
        MaterialSlotRequirement? materialSlotRequirement = null)
    {
        OperationId = ProductionRunText.Required(operationId, nameof(operationId));
        StationSystemId = ProductionRunText.Required(stationSystemId, nameof(stationSystemId));
        StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
        ProcessDefinitionId = processDefinitionId
            ?? throw new ArgumentNullException(nameof(processDefinitionId));
        ProcessVersionId = processVersionId
            ?? throw new ArgumentNullException(nameof(processVersionId));
        ConfigurationSnapshotId = configurationSnapshotId
            ?? throw new ArgumentNullException(nameof(configurationSnapshotId));
        RecipeSnapshotId = recipeSnapshotId
            ?? throw new ArgumentNullException(nameof(recipeSnapshotId));

        var requirements = resourceRequirements?.ToArray() ??
            [new ResourceRequirement(ResourceKind.Station, StationSystemId)];
        if (requirements.Length == 0 || requirements.Any(static requirement => requirement is null))
        {
            throw new ArgumentException(
                "An operation requires at least one non-null resource.",
                nameof(resourceRequirements));
        }

        if (!requirements.Any(requirement =>
                requirement.Kind == ResourceKind.Station
                && string.Equals(requirement.ResourceId, StationSystemId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "An operation must lease its station system resource.",
                nameof(resourceRequirements));
        }

        if (requirements.Distinct().Count() != requirements.Length)
        {
            throw new ArgumentException(
                "Operation resource requirements must be unique.",
                nameof(resourceRequirements));
        }

        ResourceRequirements = requirements
            .OrderBy(static requirement => requirement.Kind)
            .ThenBy(static requirement => requirement.ResourceId, StringComparer.Ordinal)
            .ToArray();
        MaterialSlotRequirement = materialSlotRequirement;
    }

    public string OperationId { get; }

    public string StationSystemId { get; }

    public StationId StationId { get; }

    public ProcessDefinitionId ProcessDefinitionId { get; }

    public ProcessVersionId ProcessVersionId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public RecipeSnapshotId RecipeSnapshotId { get; }

    public IReadOnlyList<ResourceRequirement> ResourceRequirements { get; }

    public MaterialSlotRequirement? MaterialSlotRequirement { get; }
}
