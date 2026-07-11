using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Execution;

public sealed class ProductionRunCoordinator(
    IProductionRunRepository repository,
    IResourceLeaseRepository resourceLeases,
    IStationSafetyController stationSafety,
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

        var run = ProductionRun.Create(
            request.RunId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId,
            request.ProductionLineDefinitionId,
            request.ProductionUnitIdentity,
            request.LotId,
            request.CarrierId,
            request.ActorId,
            request.EntryOperationId,
            clock.UtcNow,
            request.Operations.Select(static operation => operation.Definition),
            request.RouteTransitions);
        var plan = new ProductionRunExecutionPlan(request.RunId, request.Operations);
        var events = run.DomainEvents.ToArray();
        if (!await repository.TryAddAsync(run, plan, cancellationToken).ConfigureAwait(false))
        {
            var existing = await repository.GetByIdAsync(request.RunId, cancellationToken)
                .ConfigureAwait(false);
            return existing is not null && HasSameIdentity(existing.Run, request)
                ? Result.Success(existing.Run.ToSnapshot())
                : Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                    "Runtime.ProductionRunIdentityMismatch",
                    $"Production Run id {request.RunId} already belongs to another immutable identity."));
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
        var reason = command.Reason ?? $"Requested by {command.ActorId}.";
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
        }

        var result = command.Command switch
        {
            ProductionRunCommand.Pause => run.Pause(clock.UtcNow),
            ProductionRunCommand.Continue => run.Continue(clock.UtcNow),
            ProductionRunCommand.Stop => run.Stop(reason, clock.UtcNow),
            ProductionRunCommand.Hold => run.Hold(reason, clock.UtcNow),
            ProductionRunCommand.Release => run.Release(clock.UtcNow),
            ProductionRunCommand.Rework => run.Rework(command.OperationId!, clock.UtcNow),
            ProductionRunCommand.Scrap => run.Scrap(reason, clock.UtcNow),
            ProductionRunCommand.SafeStop => run.SafeStop(reason, clock.UtcNow),
            ProductionRunCommand.Abort => run.Stop(reason, clock.UtcNow),
            ProductionRunCommand.Reconcile => Reconcile(run, reason),
            ProductionRunCommand.Retry => Retry(run, command.OperationId, reason),
            _ => throw new InvalidOperationException($"Unsupported Production Run command {command.Command}.")
        };
        if (!result.Succeeded)
        {
            return Result.Failure<ProductionRunSnapshot>(ApplicationError.Conflict(
                result.Code,
                result.Message));
        }

        var events = run.DomainEvents.ToArray();
        await repository.SaveAsync(run, entry.Revision, CancellationToken.None).ConfigureAwait(false);
        await PublishAsync(run, events, CancellationToken.None).ConfigureAwait(false);

        if (command.Command is ProductionRunCommand.Stop
            or ProductionRunCommand.SafeStop
            or ProductionRunCommand.Abort
            or ProductionRunCommand.Scrap
            or ProductionRunCommand.Reconcile
            or ProductionRunCommand.Retry)
        {
            foreach (var operation in run.Operations)
            {
                await resourceLeases.ReleaseAsync(run.Id, operation.OperationRunId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return Result.Success(run.ToSnapshot());
    }

    private RuntimeOperationResult Reconcile(ProductionRun run, string reason)
    {
        if (run.ControlState != ProductionRunControlState.RecoveryRequired)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionRunReconcileRejected",
                $"Production Run {run.Id} is not awaiting reconciliation.");
        }

        return run.Stop($"Recovery reconciled without replay: {reason}", clock.UtcNow);
    }

    private RuntimeOperationResult Retry(ProductionRun run, string? operationId, string reason)
    {
        if (run.ControlState != ProductionRunControlState.RecoveryRequired || operationId is null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionRunRetryRejected",
                "Retry requires a recovery-required run and an explicit operation id.");
        }

        return run.RetryRecovery(operationId, reason, clock.UtcNow);
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
        && string.Equals(run.ProductionLineDefinitionId, request.ProductionLineDefinitionId, StringComparison.Ordinal)
        && run.ProductionUnitIdentity == request.ProductionUnitIdentity
        && string.Equals(run.LotId, request.LotId, StringComparison.Ordinal)
        && string.Equals(run.CarrierId, request.CarrierId, StringComparison.Ordinal)
        && string.Equals(run.ActorId, request.ActorId, StringComparison.Ordinal)
        && string.Equals(run.EntryOperationId, request.EntryOperationId, StringComparison.Ordinal);
}
