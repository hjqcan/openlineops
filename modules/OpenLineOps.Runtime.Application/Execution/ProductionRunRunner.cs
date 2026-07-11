using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class ProductionRunRunner(
    IProductionRunRepository runRepository,
    IProductionRunExecutionPlanRepository planRepository,
    IResourceLeaseRepository resourceLeases,
    IStationOperationDispatcher stationDispatcher,
    IRuntimeDomainEventPublisher domainEventPublisher,
    IRuntimeIdProvider idProvider,
    IClock clock) : IProductionRunRunner
{
    private static readonly TimeSpan ResourceLeaseSafetyMargin = TimeSpan.FromMinutes(5);

    public async ValueTask<Result<ProductionRunRunResult>> ExecuteAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        var entry = await runRepository.GetByIdAsync(runId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return Failure(ApplicationError.NotFound(
                "Runtime.ProductionRunNotFound",
                $"Production Run {runId} does not exist."));
        }

        var plan = await planRepository.GetByRunIdAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            return Failure(ApplicationError.Conflict(
                "Runtime.ProductionRunExecutionPlanMissing",
                $"Production Run {runId} has no frozen execution plan."));
        }

        var run = entry.Run;
        var revision = entry.Revision;
        if (run.IsTerminal || run.ControlState != ProductionRunControlState.Active)
        {
            return Result.Success(new ProductionRunRunResult(run.ToSnapshot()));
        }

        if (run.ExecutionStatus == ExecutionStatus.Pending)
        {
            var start = run.Start(clock.UtcNow);
            if (!start.Succeeded)
            {
                return Failure(start);
            }

            revision = await PersistAndPublishAsync(run, revision, cancellationToken)
                .ConfigureAwait(false);
        }

        while (!run.IsTerminal && run.ControlState == ProductionRunControlState.Active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = run.Operations
                .Where(operation => operation.ExecutionStatus == ExecutionStatus.Pending)
                .OrderBy(operation => operation.OperationRunId, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                break;
            }

            var dispatches = new List<PendingStationDispatch>(ready.Length);
            foreach (var operation in ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var executionPlan = plan.Operations.Single(candidate => string.Equals(
                    candidate.Definition.OperationId,
                    operation.OperationId,
                    StringComparison.Ordinal));
                var leases = await resourceLeases.TryAcquireAsync(
                    run.Id,
                    operation.OperationRunId,
                    operation.ResourceRequirements,
                    clock.UtcNow,
                    CalculateLeaseDuration(executionPlan),
                    cancellationToken).ConfigureAwait(false);
                if (leases is null)
                {
                    continue;
                }

                var sessionId = idProvider.NewSessionId();
                var started = run.StartOperation(
                    operation.OperationRunId,
                    sessionId,
                    leases,
                    clock.UtcNow);
                if (!started.Succeeded)
                {
                    await resourceLeases.ReleaseAsync(
                        run.Id,
                        operation.OperationRunId,
                        CancellationToken.None).ConfigureAwait(false);
                    return Failure(started);
                }

                // Persist the session link and fencing tokens before publishing a station job.
                revision = await PersistAndPublishAsync(run, revision, cancellationToken)
                    .ConfigureAwait(false);

                var runSnapshot = run.ToSnapshot();
                var request = new StationOperationDispatchRequest(
                    runSnapshot,
                    runSnapshot.Operations.Single(candidate => string.Equals(
                        candidate.OperationRunId,
                        operation.OperationRunId,
                        StringComparison.Ordinal)),
                    executionPlan,
                    sessionId,
                    leases);
                // Start every ready Station operation before awaiting any one of them. Route
                // results are still folded back into the aggregate serially below.
                dispatches.Add(new PendingStationDispatch(
                    operation,
                    DispatchCapturingAsync(request, cancellationToken)));
            }

            if (dispatches.Count == 0)
            {
                break;
            }

            await Task.WhenAll(dispatches.Select(static dispatch => dispatch.Outcome))
                .ConfigureAwait(false);

            RuntimeOperationResult? transitionFailure = null;
            var uncertainDispatches = new List<PendingStationDispatch>();
            foreach (var dispatch in dispatches)
            {
                var outcome = await dispatch.Outcome.ConfigureAwait(false);
                if (outcome.Result is not null)
                {
                    var transition = ApplyDispatchResult(run, dispatch.Operation, outcome.Result);
                    if (!transition.Succeeded && transitionFailure is null)
                    {
                        transitionFailure = transition;
                    }
                }
                else
                {
                    uncertainDispatches.Add(dispatch);
                }
            }

            if (uncertainDispatches.Count > 0 && !run.IsTerminal)
            {
                var exceptionNames = uncertainDispatches
                    .Select(static dispatch => dispatch.Outcome.Result.Exception?.GetType().Name)
                    .Where(static name => name is not null)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal);
                var reason = cancellationToken.IsCancellationRequested
                    ? "Station operation dispatch was interrupted; no hardware command was replayed."
                    : $"Station dispatcher failed with {string.Join(", ", exceptionNames)}; no hardware command was replayed.";
                var recovery = run.MarkRecoveryRequired(reason, clock.UtcNow);
                if (!recovery.Succeeded && transitionFailure is null)
                {
                    transitionFailure = recovery;
                }
            }

            try
            {
                revision = await PersistAndPublishAsync(run, revision, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                foreach (var dispatch in dispatches)
                {
                    await resourceLeases.HoldForRecoveryAsync(
                        run.Id,
                        dispatch.Operation.OperationRunId,
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }

            foreach (var dispatch in dispatches)
            {
                var outcome = await dispatch.Outcome.ConfigureAwait(false);
                if (outcome.Result is null)
                {
                    await resourceLeases.HoldForRecoveryAsync(
                        run.Id,
                        dispatch.Operation.OperationRunId,
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await resourceLeases.ReleaseAsync(
                        run.Id,
                        dispatch.Operation.OperationRunId,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (transitionFailure is not null)
            {
                return Failure(transitionFailure);
            }
        }

        return Result.Success(new ProductionRunRunResult(run.ToSnapshot()));
    }

    private static RuntimeOperationResult ApplyDispatchResult(
        ProductionRun run,
        OperationRun operation,
        StationOperationDispatchResult result)
    {
        if (result.ExecutionStatus == ExecutionStatus.Completed)
        {
            return run.CompleteOperation(
                operation.OperationRunId,
                result.Judgement,
                result.Outputs,
                result.CompletedStepCount,
                result.CommandCount,
                result.IncidentCount,
                result.CompletedAtUtc);
        }

        if (result.ExecutionStatus == ExecutionStatus.Canceled)
        {
            return run.Stop(
                result.FailureReason ?? "Station operation was canceled.",
                result.CompletedAtUtc);
        }

        return run.FailOperation(
            operation.OperationRunId,
            result.ExecutionStatus,
            result.FailureCode ?? "Runtime.StationOperationFailed",
            result.FailureReason ?? "Station operation failed without a reason.",
            result.CompletedStepCount,
            result.CommandCount,
            result.IncidentCount,
            result.CompletedAtUtc);
    }

    private async Task<StationDispatchOutcome> DispatchCapturingAsync(
        StationOperationDispatchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await stationDispatcher.DispatchAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return new StationDispatchOutcome(result, null);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            return new StationDispatchOutcome(null, exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new StationDispatchOutcome(null, exception);
        }
    }

    private async ValueTask<long> PersistAndPublishAsync(
        ProductionRun run,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var events = run.DomainEvents.ToArray();
        var revision = await runRepository.SaveAsync(run, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
        if (events.Length > 0)
        {
            await domainEventPublisher.PublishAsync(events, cancellationToken).ConfigureAwait(false);
            run.ClearDomainEvents();
        }

        return revision;
    }

    private static TimeSpan CalculateLeaseDuration(OperationExecutionPlan plan)
    {
        var commandTicks = plan.FrozenExecutableProcess.Nodes.Aggregate(
            0L,
            static (ticks, node) => checked(ticks + node.Timeout.Ticks));
        return TimeSpan.FromTicks(checked(commandTicks + ResourceLeaseSafetyMargin.Ticks));
    }

    private static Result<ProductionRunRunResult> Failure(RuntimeOperationResult result) =>
        Failure(ApplicationError.Conflict(result.Code, result.Message));

    private static Result<ProductionRunRunResult> Failure(ApplicationError error) =>
        Result.Failure<ProductionRunRunResult>(error);

    private sealed record PendingStationDispatch(
        OperationRun Operation,
        Task<StationDispatchOutcome> Outcome);

    private sealed record StationDispatchOutcome(
        StationOperationDispatchResult? Result,
        Exception? Exception);
}
