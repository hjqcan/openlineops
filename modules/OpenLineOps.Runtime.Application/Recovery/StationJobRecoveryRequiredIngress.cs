using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Recovery;

public sealed class StationJobRecoveryRequiredIngress(
    IProductionRunRepository runs,
    IResourceLeaseRepository resourceLeases,
    IProductionRunSafetyTransitionStore safetyTransitions,
    IRuntimeDomainEventPublisher domainEvents,
    IClock clock)
{
    public async ValueTask HandleAsync(
        StationJobRecoveryRequired message,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        while (true)
        {
            var entry = await runs.GetByIdAsync(
                    new ProductionRunId(message.ProductionRunId),
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Station recovery-required references unknown Production Run {message.ProductionRunId:D}.");
            var run = entry.Run;
            var operation = run.Operations.SingleOrDefault(item => string.Equals(
                item.OperationRunId,
                message.OperationRunId,
                StringComparison.Ordinal));
            if (operation is null
                || operation.RuntimeSessionId?.Value != message.RuntimeSessionId
                || operation.ExecutionStatus != ExecutionStatus.Running)
            {
                throw new InvalidDataException(
                    "Station recovery-required does not identify the exact durable Running Operation.");
            }

            if (run.ControlState != ProductionRunControlState.RecoveryRequired)
            {
                var detectedAtUtc = clock.UtcNow;
                if (detectedAtUtc == default || detectedAtUtc.Offset != TimeSpan.Zero)
                {
                    throw new InvalidOperationException(
                        "Coordinator recovery clock must return non-default UTC.");
                }

                var transition = run.MarkRecoveryRequired(message.Reason, detectedAtUtc);
                if (!transition.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Production Run rejected Station recovery-required evidence: {transition.Message}");
                }

                var events = run.DomainEvents.ToArray();
                try
                {
                    await safetyTransitions.SaveWithLeaseHoldsAsync(
                            run,
                            entry.Revision,
                            CreateLeaseHolds(run),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (ProductionRunConcurrencyException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                if (events.Length > 0)
                {
                    await domainEvents.PublishAsync(events, CancellationToken.None)
                        .ConfigureAwait(false);
                    run.ClearDomainEvents();
                }

                return;
            }

            await resourceLeases.HoldForRecoveryAsync(
                    run.Id,
                    CreateLeaseHolds(run),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }
    }

    private static ProductionRunLeaseHold[] CreateLeaseHolds(ProductionRun run) =>
        run.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .Select(static operation => new ProductionRunLeaseHold(
                operation.OperationRunId,
                operation.FencingTokens
                    .Select(static pair => new ResourceLeaseHoldClaim(pair.Key, pair.Value))
                    .ToArray()))
            .ToArray();
}
