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
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class ProductionRunCoordinator(
    IProductionRunRepository repository,
    IProductionMaterialRepository materials,
    IResourceLeaseRepository resourceLeases,
    IStationSafetyController stationSafety,
    IStationOperationCanceler stationOperationCanceler,
    IRuntimeDomainEventPublisher domainEventPublisher,
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
            return ResolveExistingSubmission(existing.Run, request);
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
                return ResolveExistingSubmission(existing.Run, request);
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
                return ResolveExistingSubmission(existing.Run, request);
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
        var events = run.DomainEvents.ToArray();
        if (!await repository.TryAddAsync(
                run,
                plan,
                new ProductionRunAdmission(unit.ToSnapshot(), unitEntry.Revision),
                cancellationToken)
            .ConfigureAwait(false))
        {
            existing = await repository.GetByIdAsync(request.RunId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return ResolveExistingSubmission(existing.Run, request);
            }

            var currentUnit = await materials.GetProductionUnitAsync(
                    request.ProductionUnitId,
                    cancellationToken)
                .ConfigureAwait(false);
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                "Runtime.ProductionUnitAdmissionConflict",
                currentUnit?.Aggregate.ActiveProductionRunId is { } activeRunId
                    ? $"Production Unit {request.ProductionUnitId} is already active in Production Run {activeRunId}."
                    : $"Production Unit {request.ProductionUnitId} changed during atomic run admission; reload it before submitting."));
        }

        await PublishAsync(run, events, cancellationToken).ConfigureAwait(false);
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
            var safety = await stationSafety.RequestSafeStopAsync(
                    new StationSafetyRequest(run.ToSnapshot(), command.ActorId, reason),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!safety.Accepted)
            {
                if (run.ExecutionStatus == OpenLineOps.Runtime.Contracts.ExecutionStatus.Running)
                {
                    var recovery = run.MarkRecoveryRequired(
                        safety.FailureReason ?? "Station safety channel did not confirm Safe Stop.",
                        clock.UtcNow);
                    if (recovery.Succeeded)
                    {
                        var safetyEvents = run.DomainEvents.ToArray();
                        await repository.SaveAsync(run, entry.Revision, CancellationToken.None)
                            .ConfigureAwait(false);
                        await PublishAsync(run, safetyEvents, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }

                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    safety.FailureCode ?? "Runtime.SafeStopRejected",
                    safety.FailureReason ?? "Station safety channel rejected Safe Stop."));
            }

            var snapshot = run.ToSnapshot();
            var activeOperations = snapshot.Operations
                .Where(operation => operation.ExecutionStatus == ExecutionStatus.Running)
                .ToArray();
            if (activeOperations.Length > 0)
            {
                var requestedAtUtc = clock.UtcNow;
                var cancellationResults = await Task.WhenAll(activeOperations.Select(operation =>
                        stationOperationCanceler.CancelAsync(
                                new StationOperationCancellationRequest(
                                    snapshot,
                                    operation,
                                    command.ActorId,
                                    reason,
                                    requestedAtUtc),
                                cancellationToken)
                            .AsTask()))
                    .ConfigureAwait(false);
                var rejected = cancellationResults.FirstOrDefault(result => !result.Accepted);
                if (rejected is not null)
                {
                    var recoveryEntry = await repository.GetByIdAsync(runId, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (recoveryEntry is not null && !recoveryEntry.Run.IsTerminal)
                    {
                        var recovery = recoveryEntry.Run.MarkRecoveryRequired(
                            rejected.FailureReason
                            ?? "Safe Stop reached the station actuator, but active execution cancellation was not acknowledged.",
                            clock.UtcNow);
                        if (recovery.Succeeded)
                        {
                            var recoveryEvents = recoveryEntry.Run.DomainEvents.ToArray();
                            await repository.SaveAsync(
                                    recoveryEntry.Run,
                                    recoveryEntry.Revision,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await PublishAsync(
                                    recoveryEntry.Run,
                                    recoveryEvents,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }

                    return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                        rejected.FailureCode ?? "Runtime.SafeStopExecutionCancelRejected",
                        rejected.FailureReason
                        ?? "A Station rejected active execution cancellation during Safe Stop."));
                }

                return await WaitForCanceledTerminalAsync(
                        runId,
                        "Safe Stop",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var result = command.Command switch
        {
            ProductionRunCommand.Pause => run.Pause(clock.UtcNow),
            ProductionRunCommand.Continue => run.Continue(clock.UtcNow),
            ProductionRunCommand.Stop => run.RequestStop(reason, clock.UtcNow),
            ProductionRunCommand.Hold => run.Hold(reason, clock.UtcNow),
            ProductionRunCommand.Release => run.Release(clock.UtcNow),
            ProductionRunCommand.Rework => run.Rework(command.OperationId!, clock.UtcNow),
            ProductionRunCommand.Scrap => run.ControlState == ProductionRunControlState.RecoveryRequired
                ? command.RecoveryDecision is null
                    ? RuntimeOperationResult.Rejected(
                        "Runtime.RecoveryDecisionRequired",
                        "Scrapping a recovery-required run requires an immutable operator Recovery Decision.")
                    : run.ScrapRecovery(command.RecoveryDecision)
                : run.Scrap(reason, clock.UtcNow),
            ProductionRunCommand.SafeStop => run.SafeStop(reason, clock.UtcNow),
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
            return Result.Success(run.ToSnapshot());
        }

        await repository.SaveAsync(run, entry.Revision, CancellationToken.None).ConfigureAwait(false);
        await PublishAsync(run, events, CancellationToken.None).ConfigureAwait(false);

        if (command.Command == ProductionRunCommand.Reconcile)
        {
            await resourceLeases.ReleaseAsync(
                    run.Id,
                    command.RecoveryDecision!.OperationRunId!,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        else if ((command.Command == ProductionRunCommand.Stop && run.IsTerminal)
                 || command.Command is ProductionRunCommand.SafeStop
                     or ProductionRunCommand.Scrap
                     or ProductionRunCommand.Retry
                     or ProductionRunCommand.Abort)
        {
            foreach (var operation in run.Operations)
            {
                await resourceLeases.ReleaseAsync(run.Id, operation.OperationRunId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return Result.Success(run.ToSnapshot());
    }

    private async ValueTask<Result<ProductionRunSnapshot>> CancelAsync(
        ProductionRunPersistenceEntry entry,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
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
            return run.ExecutionStatus == ExecutionStatus.Canceled
                ? Result.Success(run.ToSnapshot())
                : Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunCancelRejected",
                    $"Production Run {run.Id} already ended as {run.ExecutionStatus}."));
        }

        var snapshot = run.ToSnapshot();
        var activeOperations = snapshot.Operations
            .Where(operation => operation.ExecutionStatus == ExecutionStatus.Running)
            .ToArray();
        if (activeOperations.Length == 0)
        {
            var canceled = run.Cancel(reason, clock.UtcNow);
            if (!canceled.Succeeded)
            {
                return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    canceled.Code,
                    canceled.Message));
            }

            var events = run.DomainEvents.ToArray();
            await repository.SaveAsync(run, entry.Revision, CancellationToken.None)
                .ConfigureAwait(false);
            await PublishAsync(run, events, CancellationToken.None).ConfigureAwait(false);
            await ReleaseOperationLeasesAsync(run).ConfigureAwait(false);
            return Result.Success(run.ToSnapshot());
        }

        var requestedAtUtc = clock.UtcNow;
        var cancellationTasks = activeOperations.Select(operation => stationOperationCanceler
                .CancelAsync(
                    new StationOperationCancellationRequest(
                        snapshot,
                        operation,
                        actorId,
                        reason,
                        requestedAtUtc),
                    cancellationToken)
                .AsTask())
            .ToArray();
        var results = await Task.WhenAll(cancellationTasks).ConfigureAwait(false);
        var rejected = results.FirstOrDefault(result => !result.Accepted);
        if (rejected is not null)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                rejected.FailureCode ?? "Runtime.ProductionRunCancelRejected",
                rejected.FailureReason ?? "A Station rejected the cancellation request."));
        }

        return await WaitForCanceledTerminalAsync(
                run.Id,
                "Operator Cancel",
                cancellationToken)
            .ConfigureAwait(false);
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

    private async ValueTask ReleaseOperationLeasesAsync(ProductionRun run)
    {
        foreach (var operation in run.Operations)
        {
            await resourceLeases.ReleaseAsync(
                    run.Id,
                    operation.OperationRunId,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

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
