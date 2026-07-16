using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Recovery;

public sealed class ProductionRunRecoveryService(
    IProductionRunRepository repository,
    IResourceLeaseRepository resourceLeases,
    IProductionRunSafetyTransitionStore safetyTransitions,
    IRuntimeDomainEventPublisher domainEventPublisher,
    IClock clock) : IProductionRunRecoveryService
{
    public async ValueTask<ProductionRunRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await repository.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
        var pending = 0;
        var resumable = 0;
        var recoveryRequired = 0;
        foreach (var entry in entries)
        {
            var run = entry.Run;
            if (run.ExecutionStatus == ExecutionStatus.Pending)
            {
                pending++;
                continue;
            }

            var interrupted = run.Operations
                .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
                .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
                .ToArray();
            if (interrupted.Length == 0)
            {
                if (run.ControlState == ProductionRunControlState.RecoveryRequired)
                {
                    recoveryRequired++;
                    continue;
                }

                // No hardware action was in flight. The coordinator may dispatch a pending operation.
                resumable++;
                continue;
            }

            IDomainEvent[] events = [];
            if (run.ControlState != ProductionRunControlState.RecoveryRequired)
            {
                var operationRunIds = string.Join(
                    ", ",
                    interrupted.Select(static operation => operation.OperationRunId));
                var transition = run.MarkRecoveryRequired(
                    $"Operation Runs {operationRunIds} were interrupted by host termination; "
                    + "device commands were not replayed.",
                    clock.UtcNow);
                if (!transition.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not protect interrupted Production Run {run.Id}: {transition.Message}");
                }

                events = run.DomainEvents.ToArray();
                await safetyTransitions.SaveWithLeaseHoldsAsync(
                        run,
                        entry.Revision,
                        CreateLeaseHolds(interrupted),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await resourceLeases.HoldForRecoveryAsync(
                        run.Id,
                        CreateLeaseHolds(interrupted),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (events.Length > 0)
            {
                await domainEventPublisher.PublishAsync(events, CancellationToken.None)
                    .ConfigureAwait(false);
                run.ClearDomainEvents();
            }

            recoveryRequired++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ProductionRunRecoveryResult(pending, resumable, recoveryRequired);
    }

    private static ProductionRunLeaseHold[] CreateLeaseHolds(
        IReadOnlyCollection<OperationRun> operations) =>
        operations
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .Select(static operation => new ProductionRunLeaseHold(
                operation.OperationRunId,
                operation.FencingTokens
                    .Select(static pair => new ResourceLeaseHoldClaim(pair.Key, pair.Value))
                    .ToArray()))
            .ToArray();
}
