using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class ProductionRunRunner(
    IProductionRunRepository runRepository,
    IProductionRunExecutionPlanRepository planRepository,
    IResourceLeaseRepository resourceLeases,
    IProductionRunSafetyTransitionStore safetyTransitions,
    IProductionOperationReadiness operationReadiness,
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
            var readinessSnapshot = run.ToSnapshot();
            var ready = readinessSnapshot.Operations
                .Where(operation => operation.ExecutionStatus == ExecutionStatus.Pending)
                .OrderBy(operation => operation.OperationRunId, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                break;
            }

            var dispatchable = new List<ReadyOperation>(ready.Length);
            ProductionOperationReadiness? recoveryRequired = null;
            foreach (var operation in ready)
            {
                var readiness = await operationReadiness.EvaluateAsync(
                        readinessSnapshot,
                        operation,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (readiness.Kind == ProductionOperationReadinessKind.RecoveryRequired)
                {
                    recoveryRequired = readiness;
                    break;
                }

                if (readiness.Kind == ProductionOperationReadinessKind.Ready)
                {
                    dispatchable.Add(new ReadyOperation(
                        operation,
                        readiness.MaterialResources,
                        readiness.EvidenceKey));
                }
            }

            if (recoveryRequired is not null)
            {
                var recovery = run.MarkRecoveryRequired(recoveryRequired.Reason, clock.UtcNow);
                if (!recovery.Succeeded)
                {
                    return Failure(recovery);
                }

                await PersistAndPublishAsync(run, revision, CancellationToken.None)
                    .ConfigureAwait(false);
                return Result.Success(new ProductionRunRunResult(run.ToSnapshot()));
            }

            var dispatches = new List<PendingStationDispatch>(dispatchable.Count);
            foreach (var readyOperation in dispatchable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var executionPlan = plan.Operations.Single(candidate => string.Equals(
                    candidate.Definition.OperationId,
                    readyOperation.Operation.Definition.OperationId,
                    StringComparison.Ordinal));
                var requiredResources = readyOperation.Operation.Definition.ResourceRequirements
                    .Concat(readyOperation.MaterialResources)
                    .Distinct()
                    .ToArray();
                var leases = await resourceLeases.TryAcquireAsync(
                    run.Id,
                    readyOperation.Operation.OperationRunId,
                    requiredResources,
                    CalculateLeaseDuration(executionPlan),
                    cancellationToken).ConfigureAwait(false);
                if (leases is null)
                {
                    continue;
                }

                var releaseAcquiredLeaseSet = true;
                try
                {
                    var confirmedReadiness = await operationReadiness.EvaluateAsync(
                            run.ToSnapshot(),
                            readyOperation.Operation,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var sameMaterialResources = confirmedReadiness.MaterialResources.ToHashSet()
                        .SetEquals(readyOperation.MaterialResources);
                    if (confirmedReadiness.Kind != ProductionOperationReadinessKind.Ready
                        || !sameMaterialResources
                        || !string.Equals(
                            confirmedReadiness.EvidenceKey,
                            readyOperation.EvidenceKey,
                            StringComparison.Ordinal))
                    {
                        if (confirmedReadiness.Kind == ProductionOperationReadinessKind.RecoveryRequired)
                        {
                            var latestForRecovery = await runRepository.GetByIdAsync(
                                    run.Id,
                                    CancellationToken.None)
                                .ConfigureAwait(false)
                                ?? throw new InvalidDataException(
                                    $"Production Run {run.Id} disappeared during material readiness recovery.");
                            var recovery = latestForRecovery.Run.MarkRecoveryRequired(
                                confirmedReadiness.Reason,
                                clock.UtcNow);
                            if (recovery.Succeeded)
                            {
                                await PersistAndPublishAsync(
                                        latestForRecovery.Run,
                                        latestForRecovery.Revision,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                            }

                            return Result.Success(new ProductionRunRunResult(
                                latestForRecovery.Run.ToSnapshot()));
                        }

                        continue;
                    }

                    var latest = await runRepository.GetByIdAsync(run.Id, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException(
                            $"Production Run {run.Id} disappeared while acquiring Station resources.");
                    if (latest.Revision != revision)
                    {
                        run = latest.Run;
                        revision = latest.Revision;
                    }

                    var operation = run.Operations.Single(candidate => string.Equals(
                        candidate.OperationRunId,
                        readyOperation.Operation.OperationRunId,
                        StringComparison.Ordinal));
                    if (run.IsTerminal
                        || run.ControlState != ProductionRunControlState.Active
                        || operation.ExecutionStatus != ExecutionStatus.Pending)
                    {
                        if (LeaseSetBelongsToOperation(operation, leases))
                        {
                            // A competing coordinator persisted this exact lease set and now owns
                            // its lifecycle. Neither the creator nor an observer may release it.
                            releaseAcquiredLeaseSet = false;
                        }

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
                        return Failure(started);
                    }

                    // Persist the session link and fencing tokens before publishing a station job.
                    try
                    {
                        revision = await PersistAndPublishAsync(run, revision, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (ProductionRunConcurrencyException)
                    {
                        var winningEntry = await runRepository.GetByIdAsync(
                                run.Id,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        var winningOperation = winningEntry?.Run.Operations.SingleOrDefault(candidate =>
                            string.Equals(
                                candidate.OperationRunId,
                                operation.OperationRunId,
                                StringComparison.Ordinal));
                        if (winningOperation is not null
                            && winningOperation.ExecutionStatus != ExecutionStatus.Pending
                            && LeaseSetBelongsToOperation(winningOperation, leases))
                        {
                            releaseAcquiredLeaseSet = false;
                        }

                        throw;
                    }

                    // The durable aggregate is now the lease owner. Cleanup must use its exact
                    // fencing claims after dispatch completion or recovery reconciliation.
                    releaseAcquiredLeaseSet = false;
                    var runSnapshot = run.ToSnapshot();
                    var inputResolution = ResolveOperationInputs(
                        runSnapshot,
                        runSnapshot.Operations.Single(candidate => string.Equals(
                            candidate.OperationRunId,
                            operation.OperationRunId,
                            StringComparison.Ordinal)),
                        executionPlan);
                    var request = new StationOperationDispatchRequest(
                        runSnapshot,
                        runSnapshot.Operations.Single(candidate => string.Equals(
                            candidate.OperationRunId,
                            operation.OperationRunId,
                            StringComparison.Ordinal)),
                        executionPlan,
                        sessionId,
                        inputResolution.Inputs,
                        leases);
                    // Start every ready Station operation before awaiting any one of them. Route
                    // results are still folded back into the aggregate serially below.
                    var outcome = inputResolution.FailureReason is null
                        ? DispatchCapturingAsync(request, cancellationToken)
                        : Task.FromResult(CoordinatorInputFailure(
                            request,
                            inputResolution.FailureReason,
                            clock.UtcNow));
                    dispatches.Add(new PendingStationDispatch(
                        operation.OperationRunId,
                        leases.Select(ResourceLeaseReleaseClaim.FromLease).ToArray(),
                        outcome));
                }
                finally
                {
                    if (releaseAcquiredLeaseSet)
                    {
                        await resourceLeases.ReleaseAsync(
                                run.Id,
                                readyOperation.Operation.OperationRunId,
                                leases.Select(ResourceLeaseReleaseClaim.FromLease).ToArray(),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }

            if (dispatches.Count == 0)
            {
                break;
            }

            await Task.WhenAll(dispatches.Select(static dispatch => dispatch.Outcome))
                .ConfigureAwait(false);

            RuntimeOperationResult? transitionFailure = null;
            var persisted = false;
            for (var attempt = 0; attempt < 8 && !persisted; attempt++)
            {
                var current = await runRepository.GetByIdAsync(run.Id, CancellationToken.None)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Production Run {run.Id} disappeared while Station operations were executing.");
                run = current.Run;
                revision = current.Revision;
                transitionFailure = null;
                var hasTransition = false;
                var uncertainDispatches = new List<PendingStationDispatch>();
                var recordedOperationResults = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dispatch in dispatches)
                {
                    var outcome = await dispatch.Outcome.ConfigureAwait(false);
                    if (outcome.Result is null)
                    {
                        uncertainDispatches.Add(dispatch);
                    }
                    else if (run.IsTerminal)
                    {
                        if (HasDurableDispatchResult(run, dispatch.OperationRunId))
                        {
                            recordedOperationResults.Add(dispatch.OperationRunId);
                        }
                        else
                        {
                            uncertainDispatches.Add(dispatch);
                        }
                    }
                    else
                    {
                        var transition = RecordDispatchResult(
                            run,
                            dispatch.OperationRunId,
                            outcome.Result);
                        hasTransition |= transition.Succeeded;
                        if (transition.Succeeded)
                        {
                            recordedOperationResults.Add(dispatch.OperationRunId);
                        }
                        else
                        {
                            uncertainDispatches.Add(dispatch);
                            transitionFailure ??= transition;
                        }
                    }
                }

                if (uncertainDispatches.Count == 0
                    && transitionFailure is null
                    && !run.IsTerminal)
                {
                    var resolution = run.ResolveDispatchWave(
                        dispatches.Select(static dispatch => dispatch.OperationRunId).ToArray());
                    hasTransition |= resolution.Succeeded;
                    if (!resolution.Succeeded)
                    {
                        transitionFailure = resolution;
                    }
                }

                if ((uncertainDispatches.Count > 0 || transitionFailure is not null)
                    && !run.IsTerminal)
                {
                    var exceptionNames = uncertainDispatches
                        .Select(static dispatch => dispatch.Outcome.Result.Exception?.GetType().Name)
                        .Where(static name => name is not null)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal);
                    var reason = transitionFailure is not null
                        ? $"A Station result could not be recorded exactly: {transitionFailure.Code}: {transitionFailure.Message}"
                        : cancellationToken.IsCancellationRequested
                        ? "Station operation dispatch was interrupted; no hardware command was replayed."
                        : $"Station dispatcher failed with {string.Join(", ", exceptionNames)}; no hardware command was replayed.";
                    var detectedAtUtc = clock.UtcNow < run.LastTransitionAtUtc
                        ? run.LastTransitionAtUtc
                        : clock.UtcNow;
                    var recovery = run.MarkRecoveryRequired(reason, detectedAtUtc);
                    hasTransition |= recovery.Succeeded;
                    if (!recovery.Succeeded && transitionFailure is null)
                    {
                        transitionFailure = recovery;
                    }
                }

                if (!hasTransition)
                {
                    persisted = true;
                    break;
                }

                try
                {
                    if (run.ControlState == ProductionRunControlState.RecoveryRequired)
                    {
                        var events = run.DomainEvents.ToArray();
                        var leaseHolds = CreateProtectedLeaseHolds(run);
                        revision = await safetyTransitions.SaveWithLeaseHoldsAsync(
                                run,
                                revision,
                                leaseHolds,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (events.Length > 0)
                        {
                            await domainEventPublisher.PublishAsync(events, CancellationToken.None)
                                .ConfigureAwait(false);
                            run.ClearDomainEvents();
                        }
                    }
                    else
                    {
                        revision = await PersistAndPublishAsync(run, revision, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    persisted = true;
                }
                catch (ProductionRunConcurrencyException) when (attempt < 7)
                {
                    continue;
                }
                catch
                {
                    await HoldDurableLeaseSetForRecoveryAsync(run.Id)
                        .ConfigureAwait(false);

                    throw;
                }
            }

            var runningOperationIds = run.Operations
                .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
                .Select(static operation => operation.OperationRunId)
                .ToHashSet(StringComparer.Ordinal);
            var dispatchesToHold = dispatches
                .Where(dispatch => runningOperationIds.Contains(dispatch.OperationRunId))
                .ToArray();

            if (dispatchesToHold.Length > 0)
            {
                await HoldDurableLeaseSetForRecoveryAsync(run.Id)
                    .ConfigureAwait(false);
            }

            foreach (var dispatch in dispatches)
            {
                if (!dispatchesToHold.Contains(dispatch))
                {
                    await ReleaseDurableTerminalDispatchLeasesAsync(run, dispatch)
                        .ConfigureAwait(false);
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

    private static RuntimeOperationResult RecordDispatchResult(
        ProductionRun run,
        string operationRunId,
        StationOperationDispatchResult result)
    {
        if (result.ExecutionStatus == ExecutionStatus.Completed)
        {
            return run.RecordOperationCompletion(
                operationRunId,
                result.Judgement,
                result.Outputs,
                result.CompletedStepCount,
                result.CommandCount,
                result.IncidentCount,
                result.CompletedAtUtc,
                result.ExecutionEvidence);
        }

        if (result.ExecutionStatus == ExecutionStatus.Canceled)
        {
            return run.RecordOperationCancellation(
                operationRunId,
                result.FailureCode ?? "Runtime.OperationCanceled",
                result.FailureReason ?? "Station operation was canceled.",
                result.CompletedStepCount,
                result.CommandCount,
                result.IncidentCount,
                result.CompletedAtUtc,
                result.ExecutionEvidence);
        }

        return run.RecordOperationFailure(
            operationRunId,
            result.ExecutionStatus,
            result.FailureCode ?? "Runtime.StationOperationFailed",
            result.FailureReason ?? "Station operation failed without a reason.",
            result.CompletedStepCount,
            result.CommandCount,
            result.IncidentCount,
            result.CompletedAtUtc,
            result.ExecutionEvidence);
    }

    private static bool HasDurableDispatchResult(
        ProductionRun run,
        string operationRunId)
    {
        var operation = run.Operations.SingleOrDefault(candidate => string.Equals(
            candidate.OperationRunId,
            operationRunId,
            StringComparison.Ordinal));
        return operation is { IsTerminal: true, ExecutionEvidence: not null };
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
        catch (StationJobDispatchQuarantinedException exception) when (exception.NeverPublished)
        {
            var completedAtUtc = exception.QuarantinedAtUtc;
            return new StationDispatchOutcome(
                new StationOperationDispatchResult(
                    ExecutionStatus.Rejected,
                    ResultJudgement.Unknown,
                    OperationExecutionEvidenceFactory.FromCoordinatorFailure(
                        request,
                        completedAtUtc,
                        "Runtime.StationDispatchQuarantined",
                        exception.Message,
                        1),
                    null,
                    0,
                    0,
                    1,
                    completedAtUtc,
                    "Runtime.StationDispatchQuarantined",
                    exception.Message),
                null);
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

    private static OperationInputResolution ResolveOperationInputs(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        OperationExecutionPlan executionPlan)
    {
        var inputs = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        foreach (var mapping in executionPlan.InputMappings)
        {
            if (!operation.SourceOperationRunBindings.TryGetValue(
                    mapping.SourceOperationId,
                    out var sourceOperationRunId))
            {
                return OperationInputResolution.Failed(
                    $"Production Context input '{mapping.TargetInputKey}' has no frozen causal binding for source Operation '{mapping.SourceOperationId}'.");
            }

            var source = run.Operations.SingleOrDefault(candidate => string.Equals(
                candidate.OperationRunId,
                sourceOperationRunId,
                StringComparison.Ordinal));
            if (source is null || source.ExecutionStatus != ExecutionStatus.Completed)
            {
                return OperationInputResolution.Failed(
                    $"Production Context input '{mapping.TargetInputKey}' requires exact completed source Operation Run '{sourceOperationRunId}'.");
            }

            if (!string.Equals(
                    source.Definition.OperationId,
                    mapping.SourceOperationId,
                    StringComparison.Ordinal))
            {
                return OperationInputResolution.Failed(
                    $"Production Context input '{mapping.TargetInputKey}' causal binding '{sourceOperationRunId}' does not belong to source Operation '{mapping.SourceOperationId}'.");
            }

            if (!source.Outputs.TryGetValue(mapping.SourceOutputKey, out var value))
            {
                return OperationInputResolution.Failed(
                    $"Production Context input '{mapping.TargetInputKey}' source Operation '{mapping.SourceOperationId}' did not produce output '{mapping.SourceOutputKey}'.");
            }

            if (value.Kind != mapping.ExpectedValueKind)
            {
                return OperationInputResolution.Failed(
                    $"Production Context input '{mapping.TargetInputKey}' expected {mapping.ExpectedValueKind}, but source Operation '{mapping.SourceOperationId}' output '{mapping.SourceOutputKey}' is {value.Kind}.");
            }

            inputs.Add(mapping.TargetInputKey, value);
        }

        return new OperationInputResolution(inputs, null);
    }

    private static StationDispatchOutcome CoordinatorInputFailure(
        StationOperationDispatchRequest request,
        string reason,
        DateTimeOffset completedAtUtc)
    {
        const string code = "Runtime.ProductionContextInputInvalid";
        return new StationDispatchOutcome(
            new StationOperationDispatchResult(
                ExecutionStatus.Failed,
                ResultJudgement.Unknown,
                OperationExecutionEvidenceFactory.FromCoordinatorFailure(
                    request,
                    completedAtUtc,
                    code,
                    reason,
                    1),
                null,
                0,
                0,
                1,
                completedAtUtc,
                code,
                reason),
            null);
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
        var executionBounds = ExecutableRuntimeProcessExecutionBounds.Calculate(
            plan.FrozenExecutableProcess);
        try
        {
            return TimeSpan.FromTicks(checked(
                executionBounds.MaximumNodeExecutionTime.Ticks
                + ResourceLeaseSafetyMargin.Ticks));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"Operation {plan.Definition.OperationId} maximum execution time cannot be represented as a finite resource lease.",
                exception);
        }
    }

    private static Result<ProductionRunRunResult> Failure(RuntimeOperationResult result) =>
        Failure(ApplicationError.Conflict(result.Code, result.Message));

    private static Result<ProductionRunRunResult> Failure(ApplicationError error) =>
        Result.Failure<ProductionRunRunResult>(error);

    private static bool LeaseSetBelongsToOperation(
        OperationRun operation,
        IReadOnlyCollection<ResourceLease> leases) =>
        operation.FencingTokens.Count == leases.Count
        && leases.All(lease =>
            operation.FencingTokens.TryGetValue(lease.Resource, out var fencingToken)
            && fencingToken == lease.FencingToken);

    private static ProductionRunLeaseHold[] CreateProtectedLeaseHolds(ProductionRun run)
    {
        var protectedOperations = run.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        if (protectedOperations.Length == 0)
        {
            return [];
        }

        var leaseHolds = protectedOperations
            .Select(CreateLeaseHold)
            .ToArray();
        return ProductionRunLeaseHold.RequireExactFor(run, leaseHolds);
    }

    private async ValueTask HoldDurableLeaseSetForRecoveryAsync(ProductionRunId runId)
    {
        var latest = await runRepository.GetByIdAsync(runId, CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Production Run {runId} disappeared while its durable lease set was being protected.");
        var leaseHolds = CreateProtectedLeaseHolds(latest.Run);
        if (leaseHolds.Length == 0)
        {
            return;
        }

        await resourceLeases.HoldForRecoveryAsync(
                runId,
                leaseHolds,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static ProductionRunLeaseHold CreateLeaseHold(OperationRun operation) =>
        new(
            operation.OperationRunId,
            operation.FencingTokens
                .OrderBy(static pair => pair.Key.CanonicalKey, StringComparer.Ordinal)
                .Select(static pair => new ResourceLeaseHoldClaim(pair.Key, pair.Value))
                .ToArray());

    private async ValueTask ReleaseDurableTerminalDispatchLeasesAsync(
        ProductionRun run,
        PendingStationDispatch dispatch)
    {
        var operation = run.Operations.Single(candidate => string.Equals(
            candidate.OperationRunId,
            dispatch.OperationRunId,
            StringComparison.Ordinal));
        if (!operation.IsTerminal || operation.ExecutionEvidence is null)
        {
            throw new ResourceLeaseOwnershipException(
                run.Id,
                operation.OperationRunId,
                "the dispatch has no durable terminal execution evidence");
        }

        var exactClaims = dispatch.ReleaseClaims.ToArray();
        var claimsAreDurable = exactClaims.Length == operation.FencingTokens.Count
            && exactClaims.Select(static claim => claim.Resource).Distinct().Count()
                == exactClaims.Length
            && exactClaims.All(claim => operation.FencingTokens.TryGetValue(
                    claim.Resource,
                    out var fencingToken)
                && fencingToken == claim.FencingToken);
        if (!claimsAreDurable)
        {
            throw new ResourceLeaseOwnershipException(
                run.Id,
                operation.OperationRunId,
                "the dispatch cleanup claims differ from the durable fencing set");
        }

        var current = await ReadDispatchLeaseSetAsync(run.Id, operation, exactClaims)
            .ConfigureAwait(false);
        if (current.Length == 0)
        {
            // Another exact cleanup won. A missing complete set is the only idempotent outcome.
            return;
        }

        if (current.Length != exactClaims.Length)
        {
            throw new ResourceLeaseOwnershipException(
                run.Id,
                operation.OperationRunId,
                "only part of the durable terminal dispatch lease set remains");
        }

        var recoveryHeld = current.All(static lease =>
            lease.ExpiresAtUtc == DateTimeOffset.MaxValue);
        var finite = current.All(static lease =>
            lease.ExpiresAtUtc != DateTimeOffset.MaxValue);
        if (!recoveryHeld && !finite)
        {
            throw new ResourceLeaseOwnershipException(
                run.Id,
                operation.OperationRunId,
                "the durable terminal dispatch lease set mixes finite and recovery-held rows");
        }

        if (recoveryHeld)
        {
            try
            {
                await resourceLeases.ReleaseRecoveryHoldAsync(
                        run.Id,
                        [CreateLeaseHold(operation)],
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (ResourceLeaseOwnershipException)
            {
                if ((await ReadDispatchLeaseSetAsync(run.Id, operation, exactClaims)
                        .ConfigureAwait(false)).Length == 0)
                {
                    return;
                }

                throw;
            }
        }
        else
        {
            await resourceLeases.ReleaseAsync(
                    run.Id,
                    operation.OperationRunId,
                    exactClaims,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        if ((await ReadDispatchLeaseSetAsync(run.Id, operation, exactClaims)
                .ConfigureAwait(false)).Length != 0)
        {
            throw new ResourceLeaseOwnershipException(
                run.Id,
                operation.OperationRunId,
                "a replacement, stale fence, or unreleased row appeared during terminal cleanup");
        }
    }

    private async ValueTask<ResourceLease[]> ReadDispatchLeaseSetAsync(
        ProductionRunId runId,
        OperationRun operation,
        IReadOnlyCollection<ResourceLeaseReleaseClaim> exactClaims)
    {
        var claimResources = exactClaims
            .Select(static claim => claim.Resource)
            .ToHashSet();
        var current = (await resourceLeases.ListAsync(CancellationToken.None)
                .ConfigureAwait(false))
            .Where(lease => claimResources.Contains(lease.Resource))
            .ToArray();
        var invalid = current.FirstOrDefault(lease => lease.ProductionRunId != runId
            || !string.Equals(
                lease.OperationRunId,
                operation.OperationRunId,
                StringComparison.Ordinal)
            || !operation.FencingTokens.TryGetValue(lease.Resource, out var fencingToken)
            || fencingToken != lease.FencingToken);
        if (invalid is not null)
        {
            throw new ResourceLeaseOwnershipException(
                runId,
                operation.OperationRunId,
                $"resource {invalid.Resource.CanonicalKey} has a replacement or stale fence");
        }

        return current;
    }

    private sealed record PendingStationDispatch(
        string OperationRunId,
        IReadOnlyCollection<ResourceLeaseReleaseClaim> ReleaseClaims,
        Task<StationDispatchOutcome> Outcome);

    private sealed record ReadyOperation(
        OperationRunSnapshot Operation,
        IReadOnlyCollection<ResourceRequirement> MaterialResources,
        string? EvidenceKey);

    private sealed record StationDispatchOutcome(
        StationOperationDispatchResult? Result,
        Exception? Exception);

    private sealed record OperationInputResolution(
        IReadOnlyDictionary<string, ProductionContextValue> Inputs,
        string? FailureReason)
    {
        public static OperationInputResolution Failed(string reason) => new(
            new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal),
            reason);
    }
}
