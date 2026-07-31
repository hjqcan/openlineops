using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class ProductionRunCoordinator(
    IProductionRunRepository repository,
    IProductionMaterialRepository materials,
    IResourceLeaseRepository resourceLeases,
    IProductionRunSafetyTransitionStore safetyTransitions,
    IStationSafetyController stationSafety,
    IStationOperationCanceler stationOperationCanceler,
    IRuntimeDomainEventPublisher domainEventPublisher,
    IProductionRunCreatedOutboxDispatcher createdOutboxDispatcher,
    IClock clock) : IProductionRunCoordinator
{
    public async ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
        SubmitProductionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = Validate(request);
        if (validation is not null)
        {
            return Result.Failure<ProductionRunSnapshot>(validation);
        }

        var existing = await repository.GetByIdAsync(request.RunId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return await ResolveExistingSubmissionAsync(existing.Run, request)
                .ConfigureAwait(false);
        }

        var unitEntry = await materials.GetProductionUnitAsync(
                request.ProductionUnitId,
                cancellationToken)
            .ConfigureAwait(false);
        if (unitEntry is null)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.NotFound(
                "Runtime.ProductionUnitNotFound",
                $"Production Unit {request.ProductionUnitId} does not exist."));
        }

        var unit = unitEntry.Aggregate;
        if (!string.Equals(
                unit.ProductModelId,
                request.FrozenProductModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                unit.IdentityKey,
                request.FrozenIdentityInputKey,
                StringComparison.Ordinal))
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionUnitPlanBindingMismatch",
                $"Production Unit {unit.Id} is {unit.ProductModelId}/{unit.IdentityKey}, not the frozen "
                + $"plan binding {request.FrozenProductModelId}/{request.FrozenIdentityInputKey}."));
        }

        if (unit.ActiveProductionRunId is not null)
        {
            existing = await repository.GetByIdAsync(request.RunId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return await ResolveExistingSubmissionAsync(existing.Run, request)
                    .ConfigureAwait(false);
            }

            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionUnitAlreadyActive",
                $"Production Unit {unit.Id} is already reserved for Production Run "
                + $"{unit.ActiveProductionRunId}."));
        }

        if (unit.Disposition != ProductDisposition.InProcess)
        {
            existing = await repository.GetByIdAsync(request.RunId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return await ResolveExistingSubmissionAsync(existing.Run, request)
                    .ConfigureAwait(false);
            }

            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionUnitRunReservationRejected",
                $"Production Unit {unit.Id} cannot enter a run from {unit.Disposition}."));
        }

        string? lotId = null;
        if (unit.LotId is { } productionLotId)
        {
            var lot = await materials.GetProductionLotAsync(productionLotId, cancellationToken)
                .ConfigureAwait(false);
            if (lot is null
                || !string.Equals(
                    lot.Aggregate.ProductModelId,
                    unit.ProductModelId,
                    StringComparison.Ordinal))
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionUnitLotMismatch",
                    $"Production Unit {unit.Id} references an absent or model-incompatible Lot "
                    + $"{productionLotId}."));
            }

            lotId = productionLotId.Value;
        }

        string? carrierId = null;
        if (unit.Location is { Kind: MaterialLocationKind.CarrierPosition, CarrierId: { } id })
        {
            var carrier = await materials.GetCarrierAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (carrier is null)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionUnitCarrierMissing",
                    $"Production Unit {unit.Id} references missing Carrier {id}."));
            }

            carrierId = id.Value;
        }

        var run = ProductionRun.Create(
            request.RunId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId,
            request.ProductionLineDefinitionId,
            request.ProductionUnitId,
            new ProductionUnitIdentity(
                unit.ProductModelId,
                unit.IdentityKey,
                unit.IdentityValue),
            lotId,
            carrierId,
            request.ActorId,
            request.EntryOperationId,
            clock.UtcNow,
            request.Operations.Select(static operation => operation.Definition),
            request.RouteTransitions);
        var plan = new ProductionRunExecutionPlan(request.RunId, request.Operations);
        // Admission is the submission commit point. Once it begins, a disconnected HTTP
        // client or a canceled headless Runner must not leave an ambiguously accepted run.
        // Store the run, frozen plan, Unit reservation, and Created event outbox atomically;
        // publication after this point is retryable and cannot turn acceptance into a failure.
        cancellationToken.ThrowIfCancellationRequested();
        if (!await repository.TryAddAsync(
                run,
                plan,
                new ProductionRunAdmission(unit.ToSnapshot(), unitEntry.Revision),
                CancellationToken.None)
            .ConfigureAwait(false))
        {
            existing = await repository.GetByIdAsync(request.RunId, CancellationToken.None)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return await ResolveExistingSubmissionAsync(existing.Run, request)
                    .ConfigureAwait(false);
            }

            var currentUnit = await materials.GetProductionUnitAsync(
                    request.ProductionUnitId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionUnitAdmissionConflict",
                currentUnit?.Aggregate.ActiveProductionRunId is { } activeRunId
                    ? $"Production Unit {request.ProductionUnitId} is already active in Production Run {activeRunId}."
                    : $"Production Unit {request.ProductionUnitId} changed during atomic run admission; reload it before submitting."));
        }

        run.ClearDomainEvents();
        await TryDrainCreatedOutboxAsync().ConfigureAwait(false);
        return Result.Success(run.ToSnapshot());
    }

    public async ValueTask<Result<ProductionRunSnapshot>> CommandAsync(
        ProductionRunId runId,
        ProductionRunCommandRequest command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var entry = await repository.GetByIdAsync(runId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.NotFound(
                "Runtime.ProductionRunNotFound",
                $"Production Run {runId} does not exist."));
        }

        var run = entry.Run;
        var reason = command.RecoveryDecision?.Reason
            ?? command.Reason
            ?? $"Requested by {command.ActorId}.";
        if (command.RecoveryDecision is { } recoveryDecision
            && recoveryDecision.DecidedAtUtc > clock.UtcNow)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Validation(
                "Runtime.RecoveryDecisionFromFuture",
                $"Recovery Decision {recoveryDecision.DecisionId:D} cannot be recorded in the future."));
        }
        if (command.Command == ProductionRunCommand.Cancel)
        {
            return await CancelAsync(
                    entry,
                    command.ActorId,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (command.Command == ProductionRunCommand.SafeStop)
        {
            return await SafeStopAsync(
                    entry,
                    command.ActorId,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (run.ControlState == ProductionRunControlState.StopRequested
            && run.SafeStopRequestedAtUtc is not null)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionRunSafeStopInProgress",
                $"Production Run {run.Id} has a durable Safe Stop barrier; only the in-flight "
                + "Safe Stop or the independent Emergency Stop channel may proceed."));
        }

        if (command.Command == ProductionRunCommand.Scrap
            && run.ControlState != ProductionRunControlState.RecoveryRequired)
        {
            return await ScrapAsync(
                    entry,
                    command.ActorId,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var result = command.Command switch
        {
            ProductionRunCommand.Pause => run.Pause(clock.UtcNow),
            ProductionRunCommand.Continue => run.Continue(clock.UtcNow),
            ProductionRunCommand.Stop => run.RequestStop(reason, clock.UtcNow),
            ProductionRunCommand.Hold => run.Hold(reason, clock.UtcNow),
            ProductionRunCommand.Release => run.Release(clock.UtcNow),
            ProductionRunCommand.Rework => run.Rework(command.OperationId!, clock.UtcNow),
            ProductionRunCommand.Scrap => command.RecoveryDecision is null
                ? RuntimeOperationResult.Rejected(
                    "Runtime.RecoveryDecisionRequired",
                    "Scrapping a recovery-required run requires an immutable operator Recovery Decision.")
                : run.ScrapRecovery(command.RecoveryDecision),
            ProductionRunCommand.Reconcile => run.ReconcileRecovery(command.RecoveryDecision!),
            ProductionRunCommand.Retry => run.RetryRecovery(command.RecoveryDecision!),
            ProductionRunCommand.Abort => run.AbortRecovery(command.RecoveryDecision!),
            _ => throw new InvalidOperationException($"Unsupported Production Run command {command.Command}.")
        };
        if (!result.Succeeded)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                result.Code,
                result.Message));
        }

        var events = run.DomainEvents.ToArray();
        if (command.RecoveryDecision is not null && events.Length == 0)
        {
            await ReleaseRecoveryDecisionHoldsAsync(run, command).ConfigureAwait(false);
            return Result.Success(run.ToSnapshot());
        }

        try
        {
            await repository.SaveAsync(run, entry.Revision, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ProductionRunConcurrencyException)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionRunConcurrencyConflict",
                $"Production Run {run.Id} changed while {command.Command} was being persisted; reload it before issuing another command."));
        }

        await PublishAsync(run, events, CancellationToken.None).ConfigureAwait(false);

        if (command.Command == ProductionRunCommand.Reconcile)
        {
            var reconciledOperation = run.Operations.Single(operation => string.Equals(
                operation.OperationRunId,
                command.RecoveryDecision!.OperationRunId,
                StringComparison.Ordinal));
            await ReleaseRecoveryHoldAsync(run.Id, reconciledOperation).ConfigureAwait(false);
        }
        else if (command.Command == ProductionRunCommand.Stop && run.IsTerminal)
        {
            await ReleaseTerminalLeasesAsync(run).ConfigureAwait(false);
        }
        else if (command.Command is ProductionRunCommand.Retry
                     or ProductionRunCommand.Abort
                 || command.Command == ProductionRunCommand.Scrap
                     && command.RecoveryDecision is not null
                     && run.IsTerminal)
        {
            await ReleaseAllRecoveryHoldsAsync(run).ConfigureAwait(false);
        }

        return Result.Success(run.ToSnapshot());
    }

    private async ValueTask<Result<ProductionRunSnapshot>> SafeStopAsync(
        ProductionRunPersistenceEntry initialEntry,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        var entry = initialEntry;
        var requestedAtUtc = entry.Run.SafeStopRequestedAtUtc ?? clock.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var run = entry.Run;
            if (run.IsTerminal)
            {
                await ReleaseTerminalLeasesAsync(run).ConfigureAwait(false);
                return IsConfirmedSafeStop(run)
                    ? Result.Success(run.ToSnapshot())
                    : run.SafeStopRequestedAtUtc is null
                        ? SafeStopAlreadyEnded(run)
                        : SafeStopLostRace(run);
            }

            var barrierWasAlreadyPersisted = run.SafeStopRequestedAtUtc is not null;
            var barrier = run.RequestSafeStop(actorId, reason, requestedAtUtc);
            if (!barrier.Succeeded)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    barrier.Code,
                    barrier.Message));
            }

            if (barrierWasAlreadyPersisted)
            {
                break;
            }

            var events = run.DomainEvents.ToArray();
            try
            {
                var revision = await SaveProtectedTransitionAsync(
                        run,
                        entry.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await PublishAsync(run, events, CancellationToken.None).ConfigureAwait(false);
                entry = new ProductionRunPersistenceEntry(run, revision);
                break;
            }
            catch (ProductionRunConcurrencyException) when (attempt < 7)
            {
                entry = await repository.GetByIdAsync(run.Id, CancellationToken.None)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Production Run {run.Id} disappeared while establishing its Safe Stop barrier.");
            }
            catch (ProductionRunConcurrencyException)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunSafeStopConcurrencyConflict",
                    $"Production Run {run.Id} kept changing while its Safe Stop barrier was being established; retry the same Safe Stop command."));
            }
        }

        var barrierRun = entry.Run;
        if (barrierRun.IsTerminal)
        {
            await ReleaseTerminalLeasesAsync(barrierRun).ConfigureAwait(false);
            return IsConfirmedSafeStop(barrierRun)
                ? Result.Success(barrierRun.ToSnapshot())
                : SafeStopLostRace(barrierRun);
        }

        var barrierSnapshot = barrierRun.ToSnapshot();
        var operationsToCancel = barrierSnapshot.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        var cancellationTask = Task.WhenAll(operationsToCancel.Select(operation =>
            RequestSafeStopCancellationAsync(
                barrierSnapshot,
                operation,
                actorId,
                reason,
                requestedAtUtc)));

        // Execution cancellation is an admission barrier, while the independent Station
        // safety request is the physical acknowledgement boundary. Start cancellation first
        // so a dispatch that has been durably started but has not registered in-process is
        // pre-canceled; always attempt physical safety even when cancellation is rejected.
        StationSafetyResult safety;
        try
        {
            safety = await stationSafety.RequestSafeStopAsync(
                    new StationSafetyRequest(
                        barrierSnapshot,
                        barrierRun.SafeStopRequestedBy
                        ?? throw new InvalidDataException("Safe Stop actor evidence is missing."),
                        reason,
                        barrierRun.SafeStopRequestedAtUtc
                        ?? throw new InvalidDataException("Safe Stop timestamp evidence is missing.")),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not StackOverflowException)
        {
            return await FailSafeStopAsync(
                    barrierRun.Id,
                    "Runtime.SafeStopTransportFailed",
                    $"Station safety channel did not confirm Safe Stop: {exception.Message}")
                .ConfigureAwait(false);
        }

        if (!safety.Accepted)
        {
            return await FailSafeStopAsync(
                    barrierRun.Id,
                    safety.FailureCode ?? "Runtime.SafeStopRejected",
                    safety.FailureReason ?? "Station safety channel rejected Safe Stop.")
                .ConfigureAwait(false);
        }

        var acknowledgedAtUtc = safety.AcknowledgedAtUtc ?? clock.UtcNow;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            entry = await repository.GetByIdAsync(barrierRun.Id, CancellationToken.None)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Production Run {barrierRun.Id} disappeared after Station Safe Stop acknowledgement.");
            if (entry.Run.IsTerminal)
            {
                await ReleaseTerminalLeasesAsync(entry.Run).ConfigureAwait(false);
                return IsConfirmedSafeStop(entry.Run)
                    ? Result.Success(entry.Run.ToSnapshot())
                    : SafeStopLostRace(entry.Run);
            }

            var acknowledgement = entry.Run.AcknowledgeSafeStop(acknowledgedAtUtc);
            if (!acknowledgement.Succeeded)
            {
                return await FailSafeStopAsync(
                        entry.Run.Id,
                        acknowledgement.Code,
                        acknowledgement.Message)
                    .ConfigureAwait(false);
            }

            var acknowledgementEvents = entry.Run.DomainEvents.ToArray();
            try
            {
                var revision = await repository.SaveAsync(
                        entry.Run,
                        entry.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await PublishAsync(
                        entry.Run,
                        acknowledgementEvents,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                entry = new ProductionRunPersistenceEntry(entry.Run, revision);
                break;
            }
            catch (ProductionRunConcurrencyException) when (attempt < 7)
            {
                // Reload and preserve the Station acknowledgement evidence exactly once.
            }
            catch (ProductionRunConcurrencyException)
            {
                return await FailSafeStopAsync(
                        barrierRun.Id,
                        "Runtime.ProductionRunSafeStopConcurrencyConflict",
                        "Production Run kept changing while the Station Safe Stop acknowledgement was being recorded.")
                    .ConfigureAwait(false);
            }
        }

        var runAfterSafety = entry.Run;
        if (runAfterSafety.IsTerminal)
        {
            await ReleaseTerminalLeasesAsync(runAfterSafety).ConfigureAwait(false);
            return IsConfirmedSafeStop(runAfterSafety)
                ? Result.Success(runAfterSafety.ToSnapshot())
                : SafeStopLostRace(runAfterSafety);
        }

        if (runAfterSafety.ControlState != ProductionRunControlState.StopRequested
            || runAfterSafety.SafeStopRequestedAtUtc != barrierRun.SafeStopRequestedAtUtc)
        {
            return await FailSafeStopAsync(
                    runAfterSafety.Id,
                    "Runtime.SafeStopBarrierLost",
                    "The durable Safe Stop barrier changed before active execution cancellation.")
                .ConfigureAwait(false);
        }

        var snapshot = runAfterSafety.ToSnapshot();
        var activeOperations = snapshot.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .ToArray();
        if (activeOperations.Length == 0)
        {
            return await FailSafeStopAsync(
                    runAfterSafety.Id,
                    "Runtime.SafeStopStateInconsistent",
                    "Safe Stop remained non-terminal without an active Station operation after its actuator acknowledgement.")
                .ConfigureAwait(false);
        }

        var cancellationResults = await cancellationTask.ConfigureAwait(false);

        var rejected = cancellationResults.FirstOrDefault(result => !result.Accepted);
        if (rejected is not null)
        {
            return await FailSafeStopAsync(
                    runAfterSafety.Id,
                    rejected.FailureCode ?? "Runtime.SafeStopExecutionCancelRejected",
                    rejected.FailureReason
                    ?? "A Station rejected active execution cancellation during Safe Stop.")
                .ConfigureAwait(false);
        }

        var terminal = await WaitForCanceledTerminalAsync(
                runAfterSafety.Id,
                "Safe Stop",
                CancellationToken.None)
            .ConfigureAwait(false);
        if (terminal.IsSuccess)
        {
            await ReleaseTerminalLeasesAsync(
                    (await repository.GetByIdAsync(runAfterSafety.Id, CancellationToken.None)
                     .ConfigureAwait(false))?.Run
                    ?? throw new InvalidDataException(
                        $"Production Run {runAfterSafety.Id} disappeared after Safe Stop completion."))
                .ConfigureAwait(false);
        }

        return terminal;
    }

    private async Task<StationOperationCancellationResult> RequestSafeStopCancellationAsync(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        string actorId,
        string reason,
        DateTimeOffset requestedAtUtc)
    {
        try
        {
            return await stationOperationCanceler.CancelAsync(
                    new StationOperationCancellationRequest(
                        run,
                        operation,
                        actorId,
                        reason,
                        requestedAtUtc),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not StackOverflowException)
        {
            return StationOperationCancellationResult.Failure(
                "Runtime.SafeStopExecutionCancelFailed",
                $"Safe Stop active execution cancellation failed for Operation Run "
                + $"{operation.OperationRunId}: {exception.Message}");
        }
    }

    private async ValueTask<Result<ProductionRunSnapshot>> FailSafeStopAsync(
        ProductionRunId runId,
        string failureCode,
        string failureReason)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var entry = await repository.GetByIdAsync(runId, CancellationToken.None)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.NotFound(
                    "Runtime.ProductionRunNotFound",
                    $"Production Run {runId} disappeared while recording Safe Stop recovery."));
            }

            if (entry.Run.IsTerminal)
            {
                await ReleaseTerminalLeasesAsync(entry.Run).ConfigureAwait(false);
                return IsConfirmedSafeStop(entry.Run)
                    ? Result.Success(entry.Run.ToSnapshot())
                    : SafeStopLostRace(entry.Run);
            }

            var recovery = entry.Run.MarkRecoveryRequired(failureReason, clock.UtcNow);
            if (!recovery.Succeeded)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    failureCode,
                    failureReason));
            }

            var events = entry.Run.DomainEvents.ToArray();
            try
            {
                await SaveProtectedTransitionAsync(
                        entry.Run,
                        entry.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await PublishAsync(entry.Run, events, CancellationToken.None).ConfigureAwait(false);
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    failureCode,
                    failureReason));
            }
            catch (ProductionRunConcurrencyException) when (attempt < 7)
            {
                // Reload and preserve whichever durable transition won the race.
            }
            catch (ProductionRunConcurrencyException)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunSafeStopConcurrencyConflict",
                    $"Production Run {runId} kept changing while Safe Stop recovery was being recorded."));
            }
        }

        return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
            "Runtime.ProductionRunSafeStopConcurrencyConflict",
            $"Production Run {runId} could not persist Safe Stop recovery."));
    }

    private static bool IsConfirmedSafeStop(ProductionRun run) =>
        run.ExecutionStatus == ExecutionStatus.Canceled
        && run.ControlState == ProductionRunControlState.SafeStopped
        && run.SafeStopAcknowledgedAtUtc is not null
        && string.Equals(
            run.FailureCode,
            "Runtime.ProductionRunSafeStopped",
            StringComparison.Ordinal);

    private static Result<ProductionRunSnapshot> SafeStopLostRace(ProductionRun run) =>
        Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
            "Runtime.ProductionRunSafeStopLostRace",
            $"Production Run {run.Id} ended as {run.ExecutionStatus}/{run.ControlState} while Safe Stop was in flight."));

    private static Result<ProductionRunSnapshot> SafeStopAlreadyEnded(ProductionRun run) =>
        Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
            "Runtime.ProductionRunSafeStopRejected",
            $"Production Run {run.Id} already ended as {run.ExecutionStatus}/{run.ControlState}."));

    private async ValueTask<Result<ProductionRunSnapshot>> ScrapAsync(
        ProductionRunPersistenceEntry initialEntry,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        var entry = initialEntry;
        var requestedAtUtc = entry.Run.ScrapRequestedAtUtc ?? clock.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var run = entry.Run;
            if (run.IsTerminal)
            {
                await ReleaseTerminalLeasesAsync(run).ConfigureAwait(false);
                return IsConfirmedScrap(run, actorId, reason, requestedAtUtc)
                    ? Result.Success(run.ToSnapshot())
                    : ScrapAlreadyEnded(run);
            }

            var barrierWasAlreadyPersisted = run.ScrapRequestedAtUtc is not null;
            var barrier = run.RequestScrap(actorId, reason, requestedAtUtc);
            if (!barrier.Succeeded)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    barrier.Code,
                    barrier.Message));
            }

            if (barrierWasAlreadyPersisted)
            {
                break;
            }

            var events = run.DomainEvents.ToArray();
            try
            {
                var revision = await SaveProtectedTransitionAsync(
                        run,
                        entry.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await PublishAsync(run, events, CancellationToken.None).ConfigureAwait(false);
                entry = new ProductionRunPersistenceEntry(run, revision);
                break;
            }
            catch (ProductionRunConcurrencyException) when (attempt < 7)
            {
                entry = await repository.GetByIdAsync(run.Id, CancellationToken.None)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Production Run {run.Id} disappeared while establishing its Scrap barrier.");
            }
            catch (ProductionRunConcurrencyException)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunScrapConcurrencyConflict",
                    $"Production Run {run.Id} kept changing while its Scrap barrier was being established; retry the same Scrap command."));
            }
        }

        var barrierRun = entry.Run;
        if (barrierRun.IsTerminal)
        {
            if (!IsConfirmedScrap(barrierRun, actorId, reason, requestedAtUtc))
            {
                return ScrapAlreadyEnded(barrierRun);
            }

            await ReleaseTerminalLeasesAsync(barrierRun).ConfigureAwait(false);
            return Result.Success(barrierRun.ToSnapshot());
        }

        if (barrierRun.ControlState == ProductionRunControlState.RecoveryRequired)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionRunScrapRecoveryRequired",
                $"Production Run {barrierRun.Id} has an uncertain Scrap cancellation boundary and requires an explicit Recovery Decision."));
        }

        if (barrierRun.ControlState != ProductionRunControlState.StopRequested
            || barrierRun.ScrapRequestedAtUtc != requestedAtUtc
            || !string.Equals(barrierRun.ScrapRequestedBy, actorId, StringComparison.Ordinal)
            || !string.Equals(barrierRun.ScrapReason, reason, StringComparison.Ordinal))
        {
            return await FailScrapAsync(
                    barrierRun.Id,
                    "Runtime.ScrapBarrierLost",
                    "The durable Scrap request barrier changed before active execution cancellation.")
                .ConfigureAwait(false);
        }

        var snapshot = barrierRun.ToSnapshot();
        var activeOperations = snapshot.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        if (activeOperations.Length == 0)
        {
            return await FailScrapAsync(
                    barrierRun.Id,
                    "Runtime.ScrapStateInconsistent",
                    "Scrap remained non-terminal without an active Station operation.")
                .ConfigureAwait(false);
        }

        StationOperationCancellationResult[] cancellationResults;
        try
        {
            cancellationResults = await Task.WhenAll(activeOperations.Select(operation =>
                    stationOperationCanceler.CancelAsync(
                            new StationOperationCancellationRequest(
                                snapshot,
                                operation,
                                actorId,
                                reason,
                                requestedAtUtc),
                            CancellationToken.None)
                        .AsTask()))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not StackOverflowException)
        {
            return await FailScrapAsync(
                    barrierRun.Id,
                    "Runtime.ScrapExecutionCancelFailed",
                    $"Active execution cancellation for Scrap failed: {exception.Message}")
                .ConfigureAwait(false);
        }

        var rejected = cancellationResults.FirstOrDefault(static result => !result.Accepted);
        if (rejected is not null)
        {
            return await FailScrapAsync(
                    barrierRun.Id,
                    rejected.FailureCode ?? "Runtime.ScrapExecutionCancelRejected",
                    rejected.FailureReason
                    ?? "A Station returned an unknown rejection for active Scrap cancellation.")
                .ConfigureAwait(false);
        }

        var terminal = await WaitForScrappedTerminalAsync(barrierRun.Id).ConfigureAwait(false);
        if (terminal.IsSuccess)
        {
            var latest = await repository.GetByIdAsync(barrierRun.Id, CancellationToken.None)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Production Run {barrierRun.Id} disappeared after Scrap completion.");
            await ReleaseTerminalLeasesAsync(latest.Run).ConfigureAwait(false);
        }

        return terminal;
    }

    private async ValueTask<Result<ProductionRunSnapshot>> FailScrapAsync(
        ProductionRunId runId,
        string failureCode,
        string failureReason)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var entry = await repository.GetByIdAsync(runId, CancellationToken.None)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.NotFound(
                    "Runtime.ProductionRunNotFound",
                    $"Production Run {runId} disappeared while recording Scrap recovery."));
            }

            if (entry.Run.IsTerminal)
            {
                await ReleaseTerminalLeasesAsync(entry.Run).ConfigureAwait(false);
                return entry.Run.Disposition == ProductDisposition.Scrapped
                    ? Result.Success(entry.Run.ToSnapshot())
                    : ScrapAlreadyEnded(entry.Run);
            }

            var detectedAtUtc = clock.UtcNow < entry.Run.LastTransitionAtUtc
                ? entry.Run.LastTransitionAtUtc
                : clock.UtcNow;
            var recovery = entry.Run.MarkRecoveryRequired(failureReason, detectedAtUtc);
            if (!recovery.Succeeded)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    failureCode,
                    failureReason));
            }

            var events = entry.Run.DomainEvents.ToArray();
            try
            {
                await SaveProtectedTransitionAsync(
                        entry.Run,
                        entry.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await PublishAsync(entry.Run, events, CancellationToken.None).ConfigureAwait(false);
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    failureCode,
                    failureReason));
            }
            catch (ProductionRunConcurrencyException) when (attempt < 7)
            {
                // Reload and preserve the durable Scrap request and whichever result won.
            }
            catch (ProductionRunConcurrencyException)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunScrapConcurrencyConflict",
                    $"Production Run {runId} kept changing while Scrap recovery was being recorded."));
            }
        }

        return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
            "Runtime.ProductionRunScrapConcurrencyConflict",
            $"Production Run {runId} could not persist Scrap recovery."));
    }

    private async ValueTask<Result<ProductionRunSnapshot>> WaitForScrappedTerminalAsync(
        ProductionRunId runId)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var current = await repository.GetByIdAsync(runId, CancellationToken.None)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Production Run {runId} disappeared after Scrap cancellation was accepted.");
            if (current.Run.IsTerminal)
            {
                return current.Run.ExecutionStatus == ExecutionStatus.Completed
                    && current.Run.Judgement == ResultJudgement.Failed
                    && current.Run.Disposition == ProductDisposition.Scrapped
                    ? Result.Success(current.Run.ToSnapshot())
                    : Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                        "Runtime.ProductionRunScrapLostRace",
                        $"Production Run {runId} ended as {current.Run.ExecutionStatus}/{current.Run.Disposition} while Scrap was in flight."));
            }

            if (current.Run.ControlState == ProductionRunControlState.RecoveryRequired)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunScrapRecoveryRequired",
                    $"Production Run {runId} entered recovery while Scrap was in flight."));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None)
                .ConfigureAwait(false);
        }

        return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
            "Runtime.ProductionRunScrapPending",
            $"Station cancellation for Scrap was accepted, but Production Run {runId} has not durably settled; poll the run before issuing another command."));
    }

    private static bool IsConfirmedScrap(
        ProductionRun run,
        string actorId,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        run.ExecutionStatus == ExecutionStatus.Completed
        && run.Judgement == ResultJudgement.Failed
        && run.Disposition == ProductDisposition.Scrapped
        && string.Equals(run.ScrapRequestedBy, actorId, StringComparison.Ordinal)
        && string.Equals(run.ScrapReason, reason, StringComparison.Ordinal)
        && run.ScrapRequestedAtUtc == requestedAtUtc;

    private static Result<ProductionRunSnapshot> ScrapAlreadyEnded(ProductionRun run) =>
        Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
            "Runtime.ProductionRunScrapRejected",
            $"Production Run {run.Id} already ended as {run.ExecutionStatus}/{run.Disposition}."));

    private async ValueTask<Result<ProductionRunSnapshot>> CancelAsync(
        ProductionRunPersistenceEntry initialEntry,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        var entry = initialEntry;
        var requestedAtUtc = IsCancelBarrier(entry.Run)
            ? entry.Run.LastTransitionAtUtc
            : clock.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var run = entry.Run;
            if (run.ControlState == ProductionRunControlState.RecoveryRequired)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunCancelRejected",
                    $"Production Run {run.Id} requires an explicit Reconcile, Retry, Abort, or Scrap Recovery Decision."));
            }

            if (run.IsTerminal)
            {
                await ReleaseTerminalLeasesAsync(run).ConfigureAwait(false);
                return run.ExecutionStatus == ExecutionStatus.Canceled
                    ? Result.Success(run.ToSnapshot())
                    : Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                        "Runtime.ProductionRunCancelRejected",
                        $"Production Run {run.Id} already ended as {run.ExecutionStatus}."));
            }

            var barrierWasAlreadyPersisted = IsCancelBarrier(run);
            var barrier = run.RequestCancel(reason, requestedAtUtc);
            if (!barrier.Succeeded)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    barrier.Code,
                    barrier.Message));
            }

            if (barrierWasAlreadyPersisted)
            {
                break;
            }

            var events = run.DomainEvents.ToArray();
            try
            {
                var revision = await SaveProtectedTransitionAsync(
                        run,
                        entry.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await PublishAsync(run, events, CancellationToken.None).ConfigureAwait(false);
                entry = new ProductionRunPersistenceEntry(run, revision);
                break;
            }
            catch (ProductionRunConcurrencyException) when (attempt < 7)
            {
                entry = await repository.GetByIdAsync(run.Id, CancellationToken.None)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Production Run {run.Id} disappeared while establishing its Cancel barrier.");
            }
            catch (ProductionRunConcurrencyException)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunCancelConcurrencyConflict",
                    $"Production Run {run.Id} kept changing while its Cancel barrier was being established; retry the same Cancel command."));
            }
        }

        var barrierRun = entry.Run;
        if (barrierRun.IsTerminal)
        {
            await ReleaseTerminalLeasesAsync(barrierRun).ConfigureAwait(false);
            return barrierRun.ExecutionStatus == ExecutionStatus.Canceled
                ? Result.Success(barrierRun.ToSnapshot())
                : Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunCancelLostRace",
                    $"Production Run {barrierRun.Id} ended as {barrierRun.ExecutionStatus} while Operator Cancel was being established."));
        }

        if (!IsCancelBarrier(barrierRun)
            || !string.Equals(barrierRun.FailureReason, reason, StringComparison.Ordinal))
        {
            return await FailCancelAsync(
                    barrierRun.Id,
                    "Runtime.ProductionRunCancelBarrierLost",
                    "The durable Cancel request barrier changed before active execution cancellation.")
                .ConfigureAwait(false);
        }

        var snapshot = barrierRun.ToSnapshot();
        var activeOperations = snapshot.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        if (activeOperations.Length == 0)
        {
            return await FailCancelAsync(
                    barrierRun.Id,
                    "Runtime.ProductionRunCancelStateInconsistent",
                    "Cancel remained non-terminal without an active Station operation.")
                .ConfigureAwait(false);
        }

        StationOperationCancellationResult[] results;
        try
        {
            results = await Task.WhenAll(activeOperations.Select(operation =>
                    stationOperationCanceler.CancelAsync(
                            new StationOperationCancellationRequest(
                                snapshot,
                                operation,
                                actorId,
                                reason,
                                requestedAtUtc),
                            CancellationToken.None)
                        .AsTask()))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not StackOverflowException)
        {
            return await FailCancelAsync(
                    barrierRun.Id,
                    "Runtime.ProductionRunCancelTransportFailed",
                    $"Active execution cancellation did not reach a durable Station boundary: {exception.Message}")
                .ConfigureAwait(false);
        }

        var rejected = results.FirstOrDefault(static result => !result.Accepted);
        if (rejected is not null)
        {
            var latest = await repository.GetByIdAsync(barrierRun.Id, CancellationToken.None)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Production Run {barrierRun.Id} disappeared after Station cancellation rejection.");
            if (latest.Run.IsTerminal && latest.Run.ExecutionStatus == ExecutionStatus.Canceled)
            {
                await ReleaseTerminalLeasesAsync(latest.Run).ConfigureAwait(false);
                return Result.Success(latest.Run.ToSnapshot());
            }

            return await FailCancelAsync(
                    barrierRun.Id,
                    rejected.FailureCode ?? "Runtime.ProductionRunCancelRejected",
                    rejected.FailureReason ?? "A Station rejected the cancellation request.")
                .ConfigureAwait(false);
        }

        var terminal = await WaitForCanceledTerminalAsync(
                barrierRun.Id,
                "Operator Cancel",
                CancellationToken.None)
            .ConfigureAwait(false);
        if (terminal.IsSuccess)
        {
            var latest = await repository.GetByIdAsync(barrierRun.Id, CancellationToken.None)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Production Run {barrierRun.Id} disappeared after Operator Cancel completion.");
            await ReleaseTerminalLeasesAsync(latest.Run).ConfigureAwait(false);
        }

        return terminal;
    }

    private async ValueTask<Result<ProductionRunSnapshot>> FailCancelAsync(
        ProductionRunId runId,
        string failureCode,
        string failureReason)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var entry = await repository.GetByIdAsync(runId, CancellationToken.None)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.NotFound(
                    "Runtime.ProductionRunNotFound",
                    $"Production Run {runId} disappeared while recording Cancel recovery."));
            }

            if (entry.Run.IsTerminal)
            {
                await ReleaseTerminalLeasesAsync(entry.Run).ConfigureAwait(false);
                return entry.Run.ExecutionStatus == ExecutionStatus.Canceled
                    ? Result.Success(entry.Run.ToSnapshot())
                    : Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                        "Runtime.ProductionRunCancelLostRace",
                        $"Production Run {runId} ended as {entry.Run.ExecutionStatus} while Cancel recovery was being recorded."));
            }

            if (entry.Run.ControlState == ProductionRunControlState.RecoveryRequired)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    failureCode,
                    failureReason));
            }

            var detectedAtUtc = clock.UtcNow < entry.Run.LastTransitionAtUtc
                ? entry.Run.LastTransitionAtUtc
                : clock.UtcNow;
            var recovery = entry.Run.MarkRecoveryRequired(failureReason, detectedAtUtc);
            if (!recovery.Succeeded)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    failureCode,
                    failureReason));
            }

            var events = entry.Run.DomainEvents.ToArray();
            try
            {
                await SaveProtectedTransitionAsync(
                        entry.Run,
                        entry.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await PublishAsync(entry.Run, events, CancellationToken.None).ConfigureAwait(false);
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    failureCode,
                    failureReason));
            }
            catch (ProductionRunConcurrencyException) when (attempt < 7)
            {
                // Reload and preserve whichever durable result or recovery transition won.
            }
            catch (ProductionRunConcurrencyException)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunCancelConcurrencyConflict",
                    $"Production Run {runId} kept changing while Cancel recovery was being recorded."));
            }
        }

        return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
            "Runtime.ProductionRunCancelConcurrencyConflict",
            $"Production Run {runId} could not persist Cancel recovery."));
    }

    private async ValueTask<Result<ProductionRunSnapshot>> WaitForCanceledTerminalAsync(
        ProductionRunId runId,
        string commandName,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            var current = await repository.GetByIdAsync(runId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Production Run {runId} disappeared after Station cancellation was accepted.");
            if (current.Run.IsTerminal)
            {
                return current.Run.ExecutionStatus == ExecutionStatus.Canceled
                    ? Result.Success(current.Run.ToSnapshot())
                    : Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                        "Runtime.ProductionRunCancelLostRace",
                        $"Production Run {runId} ended as {current.Run.ExecutionStatus} while {commandName} was in flight."));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                .ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
            "Runtime.ProductionRunCancelPending",
            $"Station cancellation for {commandName} was accepted, but Production Run {runId} has not reached durable terminal state; poll the run before issuing another command."));
    }

    private async ValueTask PublishAsync(
        ProductionRun run,
        OpenLineOps.Domain.Abstractions.Events.IDomainEvent[] events,
        CancellationToken cancellationToken)
    {
        if (events.Length > 0)
        {
            await domainEventPublisher.PublishAsync(events, cancellationToken).ConfigureAwait(false);
            run.ClearDomainEvents();
        }
    }

    private async ValueTask<Result<ProductionRunSnapshot>> ResolveExistingSubmissionAsync(
        ProductionRun run,
        SubmitProductionRunRequest request)
    {
        var result = ResolveExistingSubmission(run, request);
        if (result.IsSuccess)
        {
            await TryDrainCreatedOutboxAsync().ConfigureAwait(false);
        }

        return result;
    }

    private async ValueTask TryDrainCreatedOutboxAsync()
    {
        try
        {
            await createdOutboxDispatcher.DrainAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Admission is already committed. The dispatcher recorded the failure against
            // the durable outbox item, and the hosted retry loop or a submission retry will
            // deliver the same event identity later.
        }
    }

    private async ValueTask ReleaseOperationLeasesAsync(ProductionRun run)
    {
        foreach (var operation in run.Operations)
        {
            await ReleaseOperationLeaseAsync(run.Id, operation).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseTerminalLeasesAsync(ProductionRun run)
    {
        if (!run.IsTerminal)
        {
            throw new InvalidOperationException(
                $"Production Run {run.Id} must be terminal before terminal lease cleanup.");
        }

        await ReleaseAllRecoveryHoldsAsync(run).ConfigureAwait(false);
        await ReleaseOperationLeasesAsync(run).ConfigureAwait(false);
    }

    private async ValueTask ReleaseOperationLeaseAsync(
        ProductionRunId runId,
        OperationRun operation)
    {
        var claims = operation.FencingTokens
            .Select(static pair => new ResourceLeaseReleaseClaim(pair.Key, pair.Value))
            .ToArray();
        if (claims.Length == 0)
        {
            return;
        }

        await resourceLeases.ReleaseAsync(
                runId,
                operation.OperationRunId,
                claims,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async ValueTask ReleaseRecoveryDecisionHoldsAsync(
        ProductionRun run,
        ProductionRunCommandRequest command)
    {
        if (command.Command == ProductionRunCommand.Reconcile)
        {
            var operation = run.Operations.Single(candidate => string.Equals(
                candidate.OperationRunId,
                command.RecoveryDecision!.OperationRunId,
                StringComparison.Ordinal));
            await ReleaseRecoveryHoldAsync(run.Id, operation).ConfigureAwait(false);
            return;
        }

        if (command.Command is ProductionRunCommand.Retry
            or ProductionRunCommand.Abort
            or ProductionRunCommand.Scrap)
        {
            await ReleaseAllRecoveryHoldsAsync(run).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseAllRecoveryHoldsAsync(ProductionRun run)
    {
        var heldOperationIds = (await resourceLeases.ListAsync(CancellationToken.None)
                .ConfigureAwait(false))
            .Where(lease => lease.ProductionRunId == run.Id
                && lease.ExpiresAtUtc == DateTimeOffset.MaxValue)
            .Select(static lease => lease.OperationRunId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (heldOperationIds.Length == 0)
        {
            return;
        }

        var holds = heldOperationIds
            .Select(operationRunId => CreateLeaseHold(run.Operations.Single(operation =>
                string.Equals(
                    operation.OperationRunId,
                    operationRunId,
                    StringComparison.Ordinal))))
            .ToArray();
        await ReleaseRecoveryHoldsIdempotentlyAsync(run.Id, holds).ConfigureAwait(false);
    }

    private async ValueTask ReleaseRecoveryHoldAsync(
        ProductionRunId runId,
        OperationRun operation)
    {
        var hasRecoveryHold = (await resourceLeases.ListAsync(CancellationToken.None)
                .ConfigureAwait(false))
            .Any(lease => lease.ProductionRunId == runId
                && string.Equals(
                    lease.OperationRunId,
                    operation.OperationRunId,
                    StringComparison.Ordinal)
                && lease.ExpiresAtUtc == DateTimeOffset.MaxValue);
        if (!hasRecoveryHold)
        {
            return;
        }

        await ReleaseRecoveryHoldsIdempotentlyAsync(
                runId,
                [CreateLeaseHold(operation)])
            .ConfigureAwait(false);
    }

    private async ValueTask ReleaseRecoveryHoldsIdempotentlyAsync(
        ProductionRunId runId,
        IReadOnlyCollection<ProductionRunLeaseHold> holds)
    {
        try
        {
            await resourceLeases.ReleaseRecoveryHoldAsync(
                    runId,
                    holds,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ResourceLeaseOwnershipException)
        {
            var operationRunIds = holds
                .Select(static hold => hold.OperationRunId)
                .ToHashSet(StringComparer.Ordinal);
            var recoveryHoldStillExists = (await resourceLeases.ListAsync(CancellationToken.None)
                    .ConfigureAwait(false))
                .Any(lease => lease.ProductionRunId == runId
                    && operationRunIds.Contains(lease.OperationRunId)
                    && lease.ExpiresAtUtc == DateTimeOffset.MaxValue);
            if (recoveryHoldStillExists)
            {
                throw;
            }
        }
    }

    private ValueTask<long> SaveProtectedTransitionAsync(
        ProductionRun run,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var leaseHolds = CreateActiveLeaseHolds(run);
        return leaseHolds.Length == 0
            ? repository.SaveAsync(run, expectedRevision, cancellationToken)
            : safetyTransitions.SaveWithLeaseHoldsAsync(
                run,
                expectedRevision,
                leaseHolds,
                cancellationToken);
    }

    private static ProductionRunLeaseHold[] CreateActiveLeaseHolds(ProductionRun run) =>
        run.Operations
            .Where(static operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .OrderBy(static operation => operation.OperationRunId, StringComparer.Ordinal)
            .Select(static operation => new ProductionRunLeaseHold(
                operation.OperationRunId,
                operation.FencingTokens
                    .Select(static pair => new ResourceLeaseHoldClaim(pair.Key, pair.Value))
                    .ToArray()))
            .ToArray();

    private static ProductionRunLeaseHold CreateLeaseHold(OperationRun operation) =>
        new(
            operation.OperationRunId,
            operation.FencingTokens
                .Select(static pair => new ResourceLeaseHoldClaim(pair.Key, pair.Value))
                .ToArray());

    private static bool IsCancelBarrier(ProductionRun run) =>
        run.ControlState == ProductionRunControlState.StopRequested
        && string.Equals(
            run.FailureCode,
            "Runtime.ProductionRunCancelRequested",
            StringComparison.Ordinal)
        && run.SafeStopRequestedAtUtc is null
        && run.ScrapRequestedAtUtc is null;

    private static ApplicationError? Validate(SubmitProductionRunRequest request)
    {
        if (request.Operations.Count == 0)
        {
            return ApplicationError.Validation(
                "Runtime.ProductionRunHasNoOperations",
                "A Production Run requires at least one operation execution plan.");
        }

        if (request.Operations.Select(operation => operation.Definition.OperationId)
            .Distinct(StringComparer.Ordinal).Count() != request.Operations.Count)
        {
            return ApplicationError.Validation(
                "Runtime.OperationIdDuplicate",
                "Operation ids must be unique in a Production Run plan.");
        }

        return null;
    }

    private static bool HasSameIdentity(
        ProductionRun run,
        SubmitProductionRunRequest request) =>
        string.Equals(run.ProjectId, request.ProjectId, StringComparison.Ordinal)
        && string.Equals(run.ApplicationId, request.ApplicationId, StringComparison.Ordinal)
        && string.Equals(run.ProjectSnapshotId, request.ProjectSnapshotId, StringComparison.Ordinal)
        && string.Equals(run.TopologyId, request.TopologyId, StringComparison.Ordinal)
        && string.Equals(run.ProductionLineDefinitionId, request.ProductionLineDefinitionId, StringComparison.Ordinal)
        && run.ProductionUnitId == request.ProductionUnitId
        && string.Equals(
            run.ProductionUnitIdentity.ModelId,
            request.FrozenProductModelId,
            StringComparison.Ordinal)
        && string.Equals(
            run.ProductionUnitIdentity.InputKey,
            request.FrozenIdentityInputKey,
            StringComparison.Ordinal)
        && string.Equals(run.ActorId, request.ActorId, StringComparison.Ordinal)
        && string.Equals(run.EntryOperationId, request.EntryOperationId, StringComparison.Ordinal);

    private static Result<ProductionRunSnapshot> ResolveExistingSubmission(
        ProductionRun run,
        SubmitProductionRunRequest request) =>
        HasSameIdentity(run, request)
            ? Result.Success(run.ToSnapshot())
            : Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionRunIdentityMismatch",
                $"Production Run id {request.RunId} already belongs to another immutable identity."));
}
