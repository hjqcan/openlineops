using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Commissioning.Application.Contracts;
using OpenLineOps.Commissioning.Application.Persistence;
using OpenLineOps.Commissioning.Application.Security;
using OpenLineOps.Commissioning.Domain.Identifiers;
using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Application.Services;

public sealed class CommissioningService(
    ICommissioningSessionRepository repository,
    ICommissioningAccessPolicy accessPolicy,
    IClock clock) : ICommissioningService
{
    public const int MaximumConcurrencyAttempts = 8;

    public async ValueTask<Result<CommissioningSessionDetails>> StartAsync(
        StartCommissioningSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Duration <= TimeSpan.Zero
            || command.Duration > CommissioningSession.MaximumDuration)
        {
            return Failure(
                ApplicationError.Validation(
                    "Commissioning.InvalidDuration",
                    $"Commissioning duration must be positive and no longer than "
                    + $"{CommissioningSession.MaximumDuration}."));
        }

        if (!await accessPolicy.CanStartAsync(
                command.ActorId,
                command.AuthorizedRole,
                command.Capabilities,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return Failure(
                ApplicationError.Conflict(
                    "Commissioning.AccessDenied",
                    "The actor and role are not authorized for the requested commissioning capabilities."));
        }

        var now = clock.UtcNow;
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var leaseHolder = await repository.GetLeaseHolderAsync(
                    command.StationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (leaseHolder is not null)
            {
                if (leaseHolder.Session.ExpiresAtUtc > now)
                {
                    return Failure(
                        ApplicationError.Conflict(
                            "Commissioning.StationLeaseConflict",
                            $"Station {command.StationId} already has an exclusive commissioning lease."));
                }

                var expiration = leaseHolder.Session.Expire(now);
                if (!expiration.Succeeded)
                {
                    return Failure(
                        ApplicationError.Conflict(expiration.Code, expiration.Message));
                }

                try
                {
                    await repository.SaveAsync(
                            leaseHolder.Session,
                            leaseHolder.Revision,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CommissioningSessionConcurrencyException)
                    when (attempt < MaximumConcurrencyAttempts)
                {
                    continue;
                }
                catch (CommissioningSessionConcurrencyException)
                {
                    return Failure(
                        ApplicationError.Conflict(
                            "Commissioning.ConcurrencyConflict",
                            $"Commissioning lease holder for station {command.StationId} "
                            + "kept changing; retry the operation."));
                }
            }

            var session = CommissioningSession.Start(
                new CommissioningSessionId(command.SessionId),
                command.StationId,
                command.ActorId,
                command.AuthorizedRole,
                command.LeaseId,
                command.FencingToken,
                now,
                now.Add(command.Duration),
                command.Capabilities);
            var added = await repository.TryAddAsync(session, cancellationToken)
                .ConfigureAwait(false);
            if (added == CommissioningAddResult.Added)
            {
                return Result.Success(ToDetails(session, revision: 0));
            }

            if (added == CommissioningAddResult.SessionAlreadyExists)
            {
                return Failure(
                    ApplicationError.Conflict(
                        "Commissioning.SessionAlreadyExists",
                        $"Commissioning session {command.SessionId} already exists."));
            }

            if (attempt == MaximumConcurrencyAttempts)
            {
                return Failure(
                    ApplicationError.Conflict(
                        "Commissioning.StationLeaseConflict",
                        $"Station {command.StationId} already has an exclusive commissioning lease."));
            }
        }

        throw new InvalidOperationException(
            "Commissioning start concurrency loop exited unexpectedly.");
    }

    public async ValueTask<Result<CommissioningSessionDetails>> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var id = new CommissioningSessionId(sessionId);
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return NotFound(id);
            }

            if (entry.Session.Status is not (
                    CommissioningSessionStatus.Active
                    or CommissioningSessionStatus.RecoveryRequired)
                || entry.Session.ExpiresAtUtc > clock.UtcNow)
            {
                return Result.Success(ToDetails(entry.Session, entry.Revision));
            }

            entry.Session.Expire(clock.UtcNow);
            try
            {
                var revision = await repository.SaveAsync(
                        entry.Session,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Success(ToDetails(entry.Session, revision));
            }
            catch (CommissioningSessionConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
        }

        return ConcurrencyFailure(id);
    }

    public ValueTask<Result<CommissioningSessionDetails>> RenewLeaseAsync(
        RenewCommissioningLeaseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Duration <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(Failure(
                ApplicationError.Validation(
                    "Commissioning.InvalidDuration",
                    "Commissioning lease renewal duration must be positive.")));
        }

        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.RenewLease(
                command.ActorId,
                command.FencingToken,
                clock.UtcNow.Add(command.Duration),
                clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> RecordDiagnosticAccessAsync(
        CommissioningSubjectCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.RecordDiagnosticAccess(
                command.ActorId,
                command.SubjectId,
                clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> StartSignalMonitoringAsync(
        CommissioningSubjectCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.StartSignalMonitoring(
                command.ActorId,
                command.SubjectId,
                clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> AuthorizeManualCommandAsync(
        AuthorizeCommissioningManualCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.AuthorizeManualCommand(
                command.ActorId,
                command.CommandId,
                command.StationMode,
                command.SafetyClass,
                command.DebugActionWhitelisted,
                clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> AuthorizeFlowStepAsync(
        CommissioningSubjectCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.AuthorizeFlowStep(
                command.ActorId,
                command.SubjectId,
                clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> SetBreakpointAsync(
        SetCommissioningBreakpointCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.SetBreakpoint(
                command.ActorId,
                command.NodeId,
                command.StationMode,
                command.DebugActionWhitelisted,
                clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> RecoverInterruptedActionAsync(
        RecoverCommissioningActionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.RecoverInterruptedAction(
                command.ActionId,
                command.IdempotencyClass,
                clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> ResolveRecoveryAsync(
        ResolveCommissioningRecoveryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.ResolveRecovery(
                command.ActorId,
                command.Disposition,
                clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> CompleteAsync(
        string sessionId,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            new CommissioningSessionId(sessionId),
            session => session.Complete(actorId, clock.UtcNow),
            cancellationToken);
    }

    public ValueTask<Result<CommissioningSessionDetails>> AbortAsync(
        AbortCommissioningSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            new CommissioningSessionId(command.SessionId),
            session => session.Abort(command.ActorId, command.Reason, clock.UtcNow),
            cancellationToken);
    }

    private async ValueTask<Result<CommissioningSessionDetails>> MutateAsync(
        CommissioningSessionId sessionId,
        Func<CommissioningSession, CommissioningOperationResult> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(mutation);
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return NotFound(sessionId);
            }

            var result = mutation(entry.Session);
            if (!result.Succeeded)
            {
                return Failure(ApplicationError.Conflict(result.Code, result.Message));
            }

            try
            {
                var revision = await repository.SaveAsync(
                        entry.Session,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Success(ToDetails(entry.Session, revision));
            }
            catch (CommissioningSessionConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
        }

        return ConcurrencyFailure(sessionId);
    }

    private static CommissioningSessionDetails ToDetails(
        CommissioningSession session,
        long revision) =>
        new(
            session.Id.Value,
            revision,
            session.StationId,
            session.RequestedBy,
            session.AuthorizedRole,
            session.LeaseId,
            session.FencingToken,
            session.StartedAtUtc,
            session.ExpiresAtUtc,
            session.Status,
            session.RecoveryReason,
            session.Capabilities.OrderBy(static capability => capability).ToArray(),
            session.AuditTrail.ToArray());

    private static Result<CommissioningSessionDetails> NotFound(
        CommissioningSessionId sessionId) =>
        Failure(
            ApplicationError.NotFound(
                "Commissioning.SessionNotFound",
                $"Commissioning session {sessionId} was not found."));

    private static Result<CommissioningSessionDetails> ConcurrencyFailure(
        CommissioningSessionId sessionId) =>
        Failure(
            ApplicationError.Conflict(
                "Commissioning.ConcurrencyConflict",
                $"Commissioning session {sessionId} kept changing; retry the operation."));

    private static Result<CommissioningSessionDetails> Failure(ApplicationError error) =>
        Result.Failure<CommissioningSessionDetails>(error);
}
