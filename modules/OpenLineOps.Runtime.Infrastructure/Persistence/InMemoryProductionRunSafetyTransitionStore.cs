using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryProductionRunSafetyTransitionStore(
    InMemoryProductionRunRepository productionRuns,
    InMemoryResourceLeaseRepository resourceLeases) : IProductionRunSafetyTransitionStore
{
    private readonly InMemoryProductionRunRepository _productionRuns =
        productionRuns ?? throw new ArgumentNullException(nameof(productionRuns));
    private readonly InMemoryResourceLeaseRepository _resourceLeases =
        resourceLeases ?? throw new ArgumentNullException(nameof(resourceLeases));

    public ValueTask<long> SaveWithLeaseHoldsAsync(
        ProductionRun run,
        long expectedRevision,
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
        CancellationToken cancellationToken = default)
    {
        var canonicalHolds = ProductionRunLeaseHold.RequireExactFor(run, leaseHolds);
        cancellationToken.ThrowIfCancellationRequested();

        // The aggregate/material gate is always acquired before the lease gate. No other
        // in-memory path retains either gate while acquiring the other, so the protected
        // transition is indivisible without introducing a lock-order cycle.
        lock (_productionRuns.CoordinationGate)
        {
            lock (_resourceLeases.CoordinationGate)
            {
                var leaseSnapshot = _resourceLeases.CaptureCoordinationSnapshot();
                try
                {
                    _resourceLeases.HoldForRecoveryAsync(
                            run.Id,
                            canonicalHolds,
                            CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                    var revision = _productionRuns.SaveAsync(
                            run,
                            expectedRevision,
                            CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                    return ValueTask.FromResult(revision);
                }
                catch
                {
                    _resourceLeases.RestoreCoordinationSnapshot(leaseSnapshot);
                    throw;
                }
            }
        }
    }
}
