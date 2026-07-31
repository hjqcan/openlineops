using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Persistence;

public sealed record ProductionRunActiveQuery
{
    public static ProductionRunActiveQuery All { get; } = new();

    public ProductionRunActiveQuery(
        string? productionLineDefinitionId = null,
        string? stationSystemId = null,
        string? slotResourceId = null)
    {
        var explicitLineId = OptionalCanonical(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        var explicitStationSystemId = OptionalCanonical(
            stationSystemId,
            nameof(stationSystemId));
        SlotResourceId = OptionalCanonical(slotResourceId, nameof(slotResourceId));

        if (SlotResourceId is null)
        {
            ProductionLineDefinitionId = explicitLineId;
            StationSystemId = explicitStationSystemId;
            return;
        }

        var slotAddress = SlotResourceId.Split('/', StringSplitOptions.None);
        if (slotAddress.Length != 3)
        {
            throw new ArgumentException(
                "slotResourceId must be the exact canonical Line/Station/Slot resource address.",
                nameof(slotResourceId));
        }

        var slotLineId = RequiredCanonical(slotAddress[0], nameof(slotResourceId));
        var slotStationSystemId = RequiredCanonical(slotAddress[1], nameof(slotResourceId));
        _ = RequiredCanonical(slotAddress[2], nameof(slotResourceId));
        if (explicitLineId is not null
            && !string.Equals(
                explicitLineId,
                slotLineId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "productionLineDefinitionId must match the Line segment of slotResourceId.",
                nameof(productionLineDefinitionId));
        }

        if (explicitStationSystemId is not null
            && !string.Equals(
                explicitStationSystemId,
                slotStationSystemId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "stationSystemId must match the Station segment of slotResourceId.",
                nameof(stationSystemId));
        }

        ProductionLineDefinitionId = explicitLineId ?? slotLineId;
        StationSystemId = explicitStationSystemId ?? slotStationSystemId;
    }

    public string? ProductionLineDefinitionId { get; }

    public string? StationSystemId { get; }

    public string? SlotResourceId { get; }

    private static string? OptionalCanonical(string? value, string parameterName) =>
        value is null ? null : RequiredCanonical(value, parameterName);

    private static string RequiredCanonical(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName);
        }

        return value;
    }
}

public static class ProductionRunActiveQueryEvaluator
{
    public static bool Matches(ProductionRun run, ProductionRunActiveQuery query)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(query);

        if (run.IsTerminal
            || query.ProductionLineDefinitionId is not null
            && !string.Equals(
                run.ProductionLineDefinitionId,
                query.ProductionLineDefinitionId,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (query.StationSystemId is null && query.SlotResourceId is null)
        {
            return true;
        }

        var activeOperations = run.Operations
            .Where(static operation => operation.ExecutionStatus is
                ExecutionStatus.Pending or ExecutionStatus.Running)
            .ToArray();
        if (activeOperations.Length > 0)
        {
            return activeOperations.Any(operation => MatchesOperation(
                operation.StationSystemId,
                operation.ResourceRequirements.Concat(operation.FencingTokens.Keys),
                query));
        }

        if (run.ExecutionStatus != ExecutionStatus.Pending || run.Operations.Count != 0)
        {
            return false;
        }

        var entryOperation = run.OperationDefinitions.Single(definition =>
            string.Equals(
                definition.OperationId,
                run.EntryOperationId,
                StringComparison.Ordinal));
        return MatchesOperation(
            entryOperation.StationSystemId,
            entryOperation.ResourceRequirements,
            query);
    }

    private static bool MatchesOperation(
        string stationSystemId,
        IEnumerable<ResourceRequirement> resources,
        ProductionRunActiveQuery query) =>
        (query.StationSystemId is null
            || string.Equals(
                stationSystemId,
                query.StationSystemId,
                StringComparison.Ordinal))
        && (query.SlotResourceId is null
            || resources.Any(resource =>
                resource.Kind == ResourceKind.Slot
                && string.Equals(
                    resource.ResourceId,
                    query.SlotResourceId,
                    StringComparison.Ordinal)));
}
