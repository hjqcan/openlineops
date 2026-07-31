using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IProductionRunExecutionPlanRepository
{
    ValueTask<ProductionRunExecutionPlan?> GetByRunIdAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default);
}

public sealed record ProductionRunExecutionPlan
{
    public ProductionRunExecutionPlan(
        ProductionRunId runId,
        IReadOnlyList<OperationExecutionPlan> operations)
    {
        RunId = runId;
        ArgumentNullException.ThrowIfNull(operations);
        if (RunId.Value == Guid.Empty
            || operations.Count == 0
            || operations.Any(static plan => plan is null))
        {
            throw new ArgumentException(
                "A Production Run execution plan requires an id and at least one operation.");
        }

        var operationIds = operations.Select(static operation => operation.Definition.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        if (operationIds.Count != operations.Count
            || operations.Any(operation => operation.InputMappings.Any(mapping =>
                !operationIds.Contains(mapping.SourceOperationId))))
        {
            throw new ArgumentException(
                "A Production Run execution plan requires unique Operations and existing input mapping sources.",
                nameof(operations));
        }

        Operations = operations.ToArray();
    }

    public ProductionRunId RunId { get; }

    public IReadOnlyList<OperationExecutionPlan> Operations { get; }
}
