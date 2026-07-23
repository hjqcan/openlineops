using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed class OperationDefinition : Entity<OperationDefinitionId>
{
    private OperationDefinition(
        OperationDefinitionId id,
        string displayName,
        string stationSystemId,
        string flowDefinitionId,
        string configurationSnapshotId,
        IEnumerable<OperationResourceBinding> resources,
        IEnumerable<OperationInputMapping> inputMappings)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        DisplayName = ProductionIdGuard.NotBlank(displayName, nameof(displayName));
        StationSystemId = ProductionIdGuard.NotBlank(stationSystemId, nameof(stationSystemId));
        FlowDefinitionId = ProductionIdGuard.NotBlank(flowDefinitionId, nameof(flowDefinitionId));
        ConfigurationSnapshotId = ProductionIdGuard.NotBlank(
            configurationSnapshotId,
            nameof(configurationSnapshotId));
        ArgumentNullException.ThrowIfNull(resources);
        var resourceArray = resources.ToArray();
        if (resourceArray.Length == 0 || resourceArray.Any(static resource => resource is null))
        {
            throw new ArgumentException(
                "Operation resources must contain non-null bindings.",
                nameof(resources));
        }

        if (resourceArray.Select(resource => resource.Id.Value)
                .Distinct(StringComparer.Ordinal).Count() != resourceArray.Length
            || resourceArray.Select(resource => resource.Id.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != resourceArray.Length)
        {
            throw new ArgumentException(
                "Operation resource BindingIds must be unique and cannot differ only by case.",
                nameof(resources));
        }

        var stationBindings = resourceArray
            .Where(resource => resource.Kind == OperationResourceKind.Station)
            .ToArray();
        if (stationBindings.Length != 1
            || stationBindings[0].Resolution != OperationResourceResolution.Fixed
            || !string.Equals(
                stationBindings[0].TopologyTargetId,
                StationSystemId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An Operation must contain exactly one Fixed Station resource equal to StationSystemId.",
                nameof(resources));
        }

        if (resourceArray.GroupBy(resource => (
                    resource.Kind,
                    resource.TopologyTargetId,
                    resource.Resolution))
                .Any(group => group.Count() != 1))
        {
            throw new ArgumentException(
                "Operation resource bindings must be semantically unique.",
                nameof(resources));
        }

        if (resourceArray.Count(resource =>
                resource.Kind == OperationResourceKind.Slot
                && resource.Resolution != OperationResourceResolution.Fixed) > 1)
        {
            throw new ArgumentException(
                "An Operation can declare at most one dynamic material Slot resource.",
                nameof(resources));
        }

        Resources = resourceArray
            .OrderBy(resource => resource.Kind)
            .ThenBy(resource => resource.TopologyTargetId, StringComparer.Ordinal)
            .ThenBy(resource => resource.Resolution)
            .ThenBy(resource => resource.Id.Value, StringComparer.Ordinal)
            .ToArray();

        ArgumentNullException.ThrowIfNull(inputMappings);
        var mappingArray = inputMappings.ToArray();
        if (mappingArray.Any(static mapping => mapping is null)
            || mappingArray.Select(static mapping => mapping.TargetInputKey)
                .Distinct(StringComparer.Ordinal).Count() != mappingArray.Length
            || mappingArray.Select(static mapping => mapping.TargetInputKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != mappingArray.Length)
        {
            throw new ArgumentException(
                "Operation input mappings require unique non-null target keys that cannot differ only by case.",
                nameof(inputMappings));
        }

        InputMappings = mappingArray
            .OrderBy(static mapping => mapping.TargetInputKey, StringComparer.Ordinal)
            .ToArray();
    }

    public string DisplayName { get; }

    public string StationSystemId { get; }

    public string FlowDefinitionId { get; }

    public string ConfigurationSnapshotId { get; }

    public IReadOnlyList<OperationResourceBinding> Resources { get; }

    public IReadOnlyList<OperationInputMapping> InputMappings { get; }

    public static OperationDefinition Create(
        OperationDefinitionId id,
        string displayName,
        string stationSystemId,
        string flowDefinitionId,
        string configurationSnapshotId,
        IEnumerable<OperationResourceBinding> resources,
        IEnumerable<OperationInputMapping> inputMappings)
    {
        return new OperationDefinition(
            id,
            displayName,
            stationSystemId,
            flowDefinitionId,
            configurationSnapshotId,
            resources,
            inputMappings);
    }
}
