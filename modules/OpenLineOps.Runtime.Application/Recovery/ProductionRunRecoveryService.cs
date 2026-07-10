using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Application.Recovery;

public sealed class ProductionRunRecoveryService : IProductionRunRecoveryService
{
    private readonly IProductionRunRepository _runRepository;
    private readonly IRuntimeSessionRepository _sessionRepository;
    private readonly IRuntimeDomainEventPublisher _domainEventPublisher;
    private readonly IClock _clock;

    public ProductionRunRecoveryService(
        IProductionRunRepository runRepository,
        IRuntimeSessionRepository sessionRepository,
        IRuntimeDomainEventPublisher domainEventPublisher,
        IClock clock)
    {
        _runRepository = runRepository;
        _sessionRepository = sessionRepository;
        _domainEventPublisher = domainEventPublisher;
        _clock = clock;
    }

    public async ValueTask<ProductionRunRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var recoverableRuns = await _runRepository.ListRecoverableAsync(cancellationToken)
            .ConfigureAwait(false);
        var canceledRuns = 0;
        var failedRuns = 0;
        var completedRuns = 0;

        foreach (var entry in recoverableRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = entry.Run;
            var transition = run.Status switch
            {
                ProductionRunStatus.Created => run.Cancel(
                    "Production run was abandoned before execution during startup recovery.",
                    0,
                    0,
                    0,
                    _clock.UtcNow),
                ProductionRunStatus.Running => await ReconcileRunningRunAsync(
                    run,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Repository returned terminal production run {run.Id} for recovery.")
            };

            if (!transition.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not recover production run {run.Id}: {transition.Code} {transition.Message}");
            }

            switch (run.Status)
            {
                case ProductionRunStatus.Canceled:
                    canceledRuns++;
                    break;
                case ProductionRunStatus.Failed:
                    failedRuns++;
                    break;
                case ProductionRunStatus.Completed:
                    completedRuns++;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Recovery left production run {run.Id} nonterminal as {run.Status}.");
            }

            var events = run.DomainEvents.ToArray();
            await _runRepository
                .SaveAsync(run, entry.Revision, CancellationToken.None)
                .ConfigureAwait(false);
            if (events.Length > 0)
            {
                await _domainEventPublisher.PublishAsync(events, CancellationToken.None)
                    .ConfigureAwait(false);
                run.ClearDomainEvents();
            }
        }

        return new ProductionRunRecoveryResult(canceledRuns, failedRuns, completedRuns);
    }

    private async ValueTask<RuntimeOperationResult> ReconcileRunningRunAsync(
        ProductionRun run,
        CancellationToken cancellationToken)
    {
        var recoveryAtUtc = _clock.UtcNow;
        var runningStage = run.Stages.SingleOrDefault(
            stage => stage.Status == ProductionStageRunStatus.Running);
        if (runningStage?.RuntimeSessionId is not { } sessionId)
        {
            return run.MarkInterrupted(
                "Production run was interrupted between stages; device commands were not replayed.",
                0,
                0,
                0,
                recoveryAtUtc);
        }

        var session = await _sessionRepository.GetByIdAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return run.MarkInterrupted(
                "Production run was interrupted before its linked Runtime session was persisted; device commands were not replayed.",
                0,
                0,
                0,
                recoveryAtUtc);
        }

        if (!session.IsTerminal)
        {
            var reason =
                "Runtime session was interrupted by process termination; device commands were not replayed.";
            var sessionTransition = session.Fail(
                recoveryAtUtc,
                "Runtime.ProductionRunInterrupted",
                reason);
            if (!sessionTransition.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not fail interrupted runtime session {session.Id}: "
                    + $"{sessionTransition.Code} {sessionTransition.Message}");
            }

            await PersistSessionAsync(session).ConfigureAwait(false);
            var interruptedMetrics = Metrics(session);
            return run.MarkInterrupted(
                reason,
                interruptedMetrics.CompletedStepCount,
                interruptedMetrics.CommandCount,
                interruptedMetrics.IncidentCount,
                recoveryAtUtc);
        }

        var metrics = Metrics(session);
        var sessionCompletedAtUtc = session.CompletedAtUtc
            ?? throw new InvalidDataException(
                $"Terminal Runtime session {session.Id} has no completion timestamp.");
        if (session.Status == RuntimeSessionStatus.Completed)
        {
            var complete = run.CompleteStage(
                runningStage.StageId,
                metrics.CompletedStepCount,
                metrics.CommandCount,
                metrics.IncidentCount,
                sessionCompletedAtUtc);
            if (!complete.Succeeded || run.IsTerminal)
            {
                return complete;
            }

            return run.MarkInterrupted(
                "A Runtime session completed before process termination, but later stages were not replayed.",
                0,
                0,
                0,
                recoveryAtUtc);
        }

        if (session.Status is RuntimeSessionStatus.Canceled or RuntimeSessionStatus.Stopped)
        {
            return run.Cancel(
                $"Linked Runtime session {session.Id} was {session.Status} during startup recovery.",
                metrics.CompletedStepCount,
                metrics.CommandCount,
                metrics.IncidentCount,
                sessionCompletedAtUtc);
        }

        return run.FailStage(
            runningStage.StageId,
            "Runtime.ProductionStageSessionFailed",
            $"Linked Runtime session {session.Id} was {session.Status} during startup recovery.",
            metrics.CompletedStepCount,
            metrics.CommandCount,
            metrics.IncidentCount,
            sessionCompletedAtUtc);
    }

    private async ValueTask PersistSessionAsync(RuntimeSession session)
    {
        var events = session.DomainEvents.ToArray();
        await _sessionRepository.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
        if (events.Length > 0)
        {
            await _domainEventPublisher.PublishAsync(events, CancellationToken.None)
                .ConfigureAwait(false);
            session.ClearDomainEvents();
        }
    }

    private static RecoveredSessionMetrics Metrics(RuntimeSession session)
    {
        return new RecoveredSessionMetrics(
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed),
            session.Commands.Count,
            session.Incidents.Count);
    }

    private readonly record struct RecoveredSessionMetrics(
        int CompletedStepCount,
        int CommandCount,
        int IncidentCount);
}
