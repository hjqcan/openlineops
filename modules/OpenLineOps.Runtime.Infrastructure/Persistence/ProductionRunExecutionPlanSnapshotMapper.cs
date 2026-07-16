using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class ProductionRunExecutionPlanSnapshotMapper
{
    internal static PersistedProductionRunExecutionPlan ToSnapshot(
        ProductionRunExecutionPlan plan) => new(
        plan.RunId.Value,
        plan.Operations.Select(operation => new PersistedOperationExecutionPlan(
            operation.Definition.OperationId,
            operation.Definition.StationSystemId,
            operation.Definition.StationId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            operation.InputMappings.Select(mapping => new PersistedOperationInputMapping(
                mapping.TargetInputKey,
                mapping.SourceOperationId,
                mapping.SourceOutputKey,
                mapping.ExpectedValueKind.ToString())).ToArray(),
            operation.Definition.ResourceRequirements.Select(requirement =>
                new PersistedExecutionResource(requirement.Kind.ToString(), requirement.ResourceId))
                .ToArray(),
            ToSnapshot(operation.Definition.MaterialSlotRequirement),
            operation.FrozenExecutableProcess)).ToArray());

    internal static ProductionRunExecutionPlan ToAggregate(
        PersistedProductionRunExecutionPlan snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.RunId == Guid.Empty || snapshot.Operations is null)
        {
            throw new InvalidDataException("Persisted Production Run execution plan is incomplete.");
        }

        return new ProductionRunExecutionPlan(
            new ProductionRunId(snapshot.RunId),
            snapshot.Operations.Select(operation => new OperationExecutionPlan(
                Text(operation.OperationId, "operation id"),
                Text(operation.StationSystemId, "station system id"),
                new StationId(Text(operation.StationId, "station id")),
                new ConfigurationSnapshotId(Text(
                    operation.ConfigurationSnapshotId,
                    "configuration snapshot id")),
                new RecipeSnapshotId(Text(operation.RecipeSnapshotId, "recipe snapshot id")),
                operation.ExecutableProcess
                    ?? throw new InvalidDataException(
                        "Persisted operation execution plan has no executable process."),
                operation.InputMappings?.Select(mapping => new OperationInputMappingPlan(
                    Text(mapping.TargetInputKey, "target input key"),
                    Text(mapping.SourceOperationId, "source operation id"),
                    Text(mapping.SourceOutputKey, "source output key"),
                    ParseValueKind(mapping.ExpectedValueKind)))
                    ?? throw new InvalidDataException(
                        "Persisted operation execution plan has no input mappings."),
                operation.Resources?.Select(resource => new ResourceRequirement(
                    ParseResourceKind(resource.Kind),
                    Text(resource.ResourceId, "resource id")))
                    ?? throw new InvalidDataException(
                        "Persisted operation execution plan has no resources."),
                ToAggregate(operation.MaterialSlotRequirement)))
                .ToArray());
    }

    private static PersistedExecutionMaterialSlot? ToSnapshot(
        MaterialSlotRequirement? requirement) => requirement is null
        ? null
        : new PersistedExecutionMaterialSlot(
            requirement.Resolution.ToString(),
            requirement.TopologyTargetId,
            requirement.EligibleSlotIds.ToArray());

    private static MaterialSlotRequirement? ToAggregate(
        PersistedExecutionMaterialSlot? requirement) => requirement is null
        ? null
        : new MaterialSlotRequirement(
            ParseMaterialSlotResolution(requirement.Resolution),
            Text(requirement.TopologyTargetId, "material Slot topology target id"),
            requirement.EligibleSlotIds
                ?? throw new InvalidDataException(
                    "Persisted material Slot policy has no eligible Slot ids."));

    private static MaterialSlotResolution ParseMaterialSlotResolution(string? value)
    {
        if (value is not null
            && Enum.TryParse<MaterialSlotResolution>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            return parsed;
        }

        throw new InvalidDataException($"Persisted material Slot resolution '{value}' is invalid.");
    }

    private static ResourceKind ParseResourceKind(string? value)
    {
        if (value is not null
            && Enum.TryParse<ResourceKind>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            return parsed;
        }

        throw new InvalidDataException($"Persisted resource kind '{value}' is invalid.");
    }

    private static ProductionContextValueKind ParseValueKind(string? value)
    {
        if (value is not null
            && Enum.TryParse<ProductionContextValueKind>(value, false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Persisted Production Context value kind '{value}' is invalid.");
    }

    private static string Text(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"Persisted Production Run execution plan has no canonical {fieldName}.")
            : value;
}

internal sealed record PersistedProductionRunExecutionPlan(
    Guid RunId,
    PersistedOperationExecutionPlan[]? Operations);

internal sealed record PersistedOperationExecutionPlan(
    string? OperationId,
    string? StationSystemId,
    string? StationId,
    string? ConfigurationSnapshotId,
    string? RecipeSnapshotId,
    PersistedOperationInputMapping[]? InputMappings,
    PersistedExecutionResource[]? Resources,
    PersistedExecutionMaterialSlot? MaterialSlotRequirement,
    ExecutableRuntimeProcess? ExecutableProcess);

internal sealed record PersistedOperationInputMapping(
    string? TargetInputKey,
    string? SourceOperationId,
    string? SourceOutputKey,
    string? ExpectedValueKind);

internal sealed record PersistedExecutionResource(string? Kind, string? ResourceId);

internal sealed record PersistedExecutionMaterialSlot(
    string? Resolution,
    string? TopologyTargetId,
    string[]? EligibleSlotIds);
