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

        Operations = operations.ToArray();
    }

    public ProductionRunId RunId { get; }

    public IReadOnlyList<OperationExecutionPlan> Operations { get; }
}
