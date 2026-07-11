using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Recovery;

public sealed class ProductionRunRecoveryService(
    IProductionRunRepository repository,
    IResourceLeaseRepository resourceLeases,
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
            cancellationToken.ThrowIfCancellationRequested();
            var run = entry.Run;
            if (run.ExecutionStatus == ExecutionStatus.Pending)
            {
                pending++;
                continue;
            }

            if (run.ControlState == ProductionRunControlState.RecoveryRequired)
            {
                recoveryRequired++;
                continue;
            }

            var interrupted = run.Operations.SingleOrDefault(operation =>
                operation.ExecutionStatus == ExecutionStatus.Running);
            if (interrupted is null)
            {
                // No hardware action was in flight. The coordinator may dispatch a pending operation.
                resumable++;
                continue;
            }

            var transition = run.MarkRecoveryRequired(
                $"Operation Run {interrupted.OperationRunId} was interrupted by host termination; "
                + "device commands were not replayed.",
                clock.UtcNow);
            if (!transition.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not protect interrupted Production Run {run.Id}: {transition.Message}");
            }

            var events = run.DomainEvents.ToArray();
            await repository.SaveAsync(run, entry.Revision, CancellationToken.None)
                .ConfigureAwait(false);
            await resourceLeases.HoldForRecoveryAsync(
                    run.Id,
                    interrupted.OperationRunId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (events.Length > 0)
            {
                await domainEventPublisher.PublishAsync(events, CancellationToken.None)
                    .ConfigureAwait(false);
                run.ClearDomainEvents();
            }

            recoveryRequired++;
        }

        return new ProductionRunRecoveryResult(pending, resumable, recoveryRequired);
    }
}
