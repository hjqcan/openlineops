using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runner;

public interface IProductionRunCompletionWaiter
{
    ValueTask<Result<ProductionRunSnapshot>> WaitForTerminalAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default);
}

public sealed class ProductionRunCompletionWaiter(
    IProductionRunRepository repository) : IProductionRunCompletionWaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async ValueTask<Result<ProductionRunSnapshot>> WaitForTerminalAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var entry = await repository.GetByIdAsync(runId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.NotFound(
                    "Runner.ProductionRunNotFound",
                    $"Production Run {runId.Value:D} disappeared after it was accepted."));
            }

            var snapshot = entry.Run.ToSnapshot();
            if (HasDurableOutcome(snapshot))
            {
                return Result.Success(snapshot);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;

    private static bool HasDurableOutcome(ProductionRunSnapshot snapshot) =>
        IsTerminal(snapshot.ExecutionStatus)
        || snapshot.ControlState == ProductionRunControlState.RecoveryRequired;
}
