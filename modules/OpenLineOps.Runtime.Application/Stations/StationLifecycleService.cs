using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Stations;

public enum StationLifecycleCommand
{
    Reset,
    Start,
    Complete,
    Hold,
    Unhold,
    Suspend,
    Unsuspend,
    Stop,
    Abort,
    Clear,
    Acknowledge
}

public sealed class StationLifecycleService(
    IStationLifecycleRepository repository,
    IClock clock,
    IStationControllerHandshakeRepository controllerHandshakeRepository,
    IStationAgentControlLeaseRepository agentControlLeaseRepository,
    IStationAgentControlLeaseValidator agentControlLeaseValidator,
    IStationRecipeStartAuthority recipeStartAuthority,
    StationControllerHandshakeOptions controllerHandshakeOptions)
{
    public const int MaximumConcurrencyAttempts = 8;

    public async ValueTask<Result<StationLifecyclePersistenceEntry>> GetAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        var entry = await repository.GetByIdAsync(stationId, cancellationToken)
            .ConfigureAwait(false);
        return entry is null
            ? NotFound(stationId)
            : Result.Success(entry);
    }

    public async ValueTask<Result<StationLifecyclePersistenceEntry>> CreateAsync(
        StationId stationId,
        StationMode mode,
        StationReadiness readiness,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(readiness);
        _ = Required(reason, nameof(reason));
        var station = StationLifecycle.Create(
            stationId,
            mode,
            readiness,
            actorId,
            clock.UtcNow);
        if (!await repository.TryAddAsync(station, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<StationLifecyclePersistenceEntry>(
                ApplicationError.Conflict(
                    "Runtime.StationLifecycleAlreadyExists",
                    $"Station lifecycle {stationId} already exists."));
        }

        return Result.Success(new StationLifecyclePersistenceEntry(station, revision: 0));
    }

    public ValueTask<Result<StationLifecyclePersistenceEntry>> ChangeModeAsync(
        StationId stationId,
        StationMode mode,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            stationId,
            actorId,
            reason,
            StationCommandGrant.ChangeMode,
            (station, authorization, occurredAtUtc) =>
                station.ChangeMode(mode, authorization, reason, occurredAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<StationLifecyclePersistenceEntry>> UpdateReadinessAsync(
        StationId stationId,
        StationReadiness readiness,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        return MutateAsync(
            stationId,
            actorId,
            reason,
            StationCommandGrant.ReportReadiness,
            (station, authorization, occurredAtUtc) =>
                station.UpdateReadiness(readiness, authorization, reason, occurredAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<StationLifecyclePersistenceEntry>> CommandAsync(
        StationId stationId,
        StationLifecycleCommand command,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                "Station lifecycle command is invalid.");
        }

        var grant = GrantFor(command);
        return MutateAsync(
            stationId,
            actorId,
            reason,
            grant,
            (station, authorization, occurredAtUtc) =>
                Execute(station, command, authorization, reason, occurredAtUtc),
            cancellationToken,
            command);
    }

    public async ValueTask<Result<StationLifecyclePersistenceEntry>> AcknowledgeAsync(
        StationId stationId,
        string ownerAgentInstanceId,
        long agentFencingToken,
        string leaseHandle,
        string? commandId,
        string? controllerSessionId,
        long? commandSequence,
        StationMode? observedMode,
        StationState? observedState,
        long? stateSequence,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        _ = Required(actorId, nameof(actorId));
        _ = Required(reason, nameof(reason));
        _ = StationAgentControlLease.RequireOwnerInstanceId(
            ownerAgentInstanceId,
            nameof(ownerAgentInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(agentFencingToken, 1);
        var authorization = new StationCommandAuthorization(
            actorId,
            StationCommandGrant.AcknowledgeTransition);

        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var callerLease = await agentControlLeaseValidator.ValidateAsync(
                    stationId,
                    actorId,
                    ownerAgentInstanceId,
                    agentFencingToken,
                    leaseHandle,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!callerLease.IsValid)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationAgentControlLeaseInvalid",
                        "The acknowledging Agent process does not own the current "
                        + "Station control lease."));
            }

            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return NotFound(stationId);
            }

            if (entry.Station.PendingControllerRecovery is not null)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerRecoverySynchronizationPending",
                        "Controller recovery must be durably synchronized before "
                        + "a Station transition can be acknowledged."));
            }

            var pending = entry.Station.PendingControllerCommand;
            RuntimeOperationResult result;
            if (pending is null)
            {
                if (controllerSessionId is null
                    || commandSequence is null
                    || observedMode is null
                    || observedState is null
                    || stateSequence is null)
                {
                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        ApplicationError.Conflict(
                            "Runtime.StationControllerStateEvidenceRequired",
                            "Exact controller session, command, mode, state, and "
                            + "state-sequence evidence is required for every "
                            + "Station transition acknowledgement."));
                }

                var evidenceError =
                    await ValidateUncommandedTransitionCompletionAsync(
                            stationId,
                            entry.Station,
                            actorId,
                            ownerAgentInstanceId,
                            agentFencingToken,
                            commandId,
                            controllerSessionId,
                            commandSequence.Value,
                            observedMode.Value,
                            observedState.Value,
                            stateSequence.Value,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (evidenceError is not null)
                {
                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        evidenceError);
                }

                result = entry.Station.AcknowledgeTransition(
                    authorization,
                    reason,
                    clock.UtcNow);
            }
            else
            {
                if (commandId is null
                    || controllerSessionId is null
                    || commandSequence is null
                    || observedMode is null
                    || observedState is null
                    || stateSequence is null)
                {
                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        ApplicationError.Conflict(
                            "Runtime.StationControllerCommandEvidenceRequired",
                            "Exact controller command, session, sequence, mode, "
                            + "state, and state-sequence evidence is required to "
                            + "acknowledge this Station transition."));
                }

                if (!string.Equals(
                        pending.OwnerAgentId,
                        actorId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        pending.OwnerAgentInstanceId,
                        ownerAgentInstanceId,
                        StringComparison.Ordinal)
                    || pending.FencingToken != agentFencingToken)
                {
                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        ApplicationError.Conflict(
                            "Runtime.StationControllerCommandLeaseFenceMismatch",
                            "The acknowledging Agent lease does not match the "
                            + "pending controller command."));
                }

                if (!DeliveryClaimMatches(
                        entry.Station.PendingControllerCommandDelivery,
                        pending))
                {
                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        ApplicationError.Conflict(
                            "Runtime.StationControllerCommandNotClaimed",
                            "The pending controller command must be durably "
                            + "claimed by its fenced Agent before completion "
                            + "can be acknowledged."));
                }

                var completion = await ValidateControllerCommandCompletionAsync(
                        stationId,
                        pending,
                        commandId,
                        controllerSessionId,
                        commandSequence.Value,
                        observedMode.Value,
                        observedState.Value,
                        stateSequence.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (completion is not null)
                {
                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        completion);
                }

                result = entry.Station.AcknowledgeControllerTransition(
                    controllerSessionId,
                    commandSequence.Value,
                    authorization,
                    reason,
                    clock.UtcNow);
            }

            if (!result.Succeeded)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(result.Code, result.Message));
            }

            try
            {
                var nextRevision = await repository.SaveAsync(
                        entry.Station,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                var persisted = new StationLifecyclePersistenceEntry(
                    entry.Station,
                    nextRevision);
                var callerStillOwnsLease =
                    await agentControlLeaseValidator.ValidateAsync(
                            stationId,
                            actorId,
                            ownerAgentInstanceId,
                            agentFencingToken,
                            leaseHandle,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (!callerStillOwnsLease.IsValid)
                {
                    if (pending is not null)
                    {
                        await AbortAfterControllerFenceLossAsync(
                                stationId,
                                pending,
                                "Agent control lease changed while the Station "
                                + "completion was committed.",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _ = await RequireRecoveryAfterUncommandedFenceLossAsync(
                                stationId,
                                "Agent control lease changed while an uncommanded "
                                + "Station completion was committed.",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        ApplicationError.Conflict(
                            "Runtime.StationControllerCompletionFenceLost",
                            "Agent control lease changed while the Station "
                            + "completion was committed."));
                }

                if (pending is null)
                {
                    var postEvidence =
                        await ValidateCommittedUncommandedCompletionAsync(
                                stationId,
                                persisted.Station,
                                actorId,
                                ownerAgentInstanceId,
                                agentFencingToken,
                                commandId,
                                controllerSessionId!,
                                commandSequence!.Value,
                                observedMode!.Value,
                                observedState!.Value,
                                stateSequence!.Value,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (postEvidence is not null)
                    {
                        _ = await RequireRecoveryAfterUncommandedFenceLossAsync(
                                stationId,
                                postEvidence.Message,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return Result.Failure<StationLifecyclePersistenceEntry>(
                            postEvidence);
                    }

                    return Result.Success(persisted);
                }

                var postCommit = await ValidateControllerCommandCompletionAsync(
                        stationId,
                        pending,
                        commandId!,
                        controllerSessionId!,
                        commandSequence!.Value,
                        observedMode!.Value,
                        observedState!.Value,
                        stateSequence!.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (postCommit is null)
                {
                    return Result.Success(persisted);
                }

                await AbortAfterControllerFenceLossAsync(
                        stationId,
                        pending,
                        postCommit.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerCommandFenceLost",
                        postCommit.Message));
            }
            catch (StationLifecycleConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
            catch (StationLifecycleConcurrencyException)
            {
                return LifecycleConcurrencyConflict(stationId);
            }
        }

        return LifecycleConcurrencyConflict(stationId);
    }

    public async ValueTask<Result<StationLifecyclePersistenceEntry>>
        GetControllerCommandForAgentAsync(
            StationId stationId,
            string ownerAgentId,
            string ownerAgentInstanceId,
            long fencingToken,
            string leaseHandle,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        _ = Required(ownerAgentId, nameof(ownerAgentId));
        _ = StationAgentControlLease.RequireOwnerInstanceId(
            ownerAgentInstanceId,
            nameof(ownerAgentInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        var validation = await agentControlLeaseValidator.ValidateAsync(
                stationId,
                ownerAgentId,
                ownerAgentInstanceId,
                fencingToken,
                leaseHandle,
                cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure<StationLifecyclePersistenceEntry>(
                ApplicationError.Conflict(
                    "Runtime.StationAgentControlLeaseInvalid",
                    "The Agent instance does not own the current Station control "
                    + "lease and cannot claim a controller command."));
        }

        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return NotFound(stationId);
            }

            if (entry.Station.PendingControllerCommand is not { } pending)
            {
                return Result.Success(entry);
            }

            if (clock.UtcNow >= pending.DeadlineUtc)
            {
                _ = await ExpireControllerCommandAsync(
                        stationId,
                        pending.CommandId,
                        cancellationToken)
                    .ConfigureAwait(false);
                var afterExpiry = await repository.GetByIdAsync(
                        stationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (afterExpiry is null)
                {
                    return NotFound(stationId);
                }

                if (afterExpiry.Station.PendingControllerCommand is null)
                {
                    return Result.Success(afterExpiry);
                }

                continue;
            }

            if (!string.Equals(
                    pending.OwnerAgentId,
                    ownerAgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    pending.OwnerAgentInstanceId,
                    ownerAgentInstanceId,
                    StringComparison.Ordinal)
                || pending.FencingToken != fencingToken)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerCommandLeaseFenceMismatch",
                        "The pending controller command is fenced to another Agent "
                        + "lease generation."));
            }

            if (entry.Station.PendingControllerCommandDelivery is null)
            {
                var handshake =
                    await controllerHandshakeRepository.GetByIdAsync(
                            stationId,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (handshake is null
                    || handshake.State.OperationalEpoch
                        != pending.IssuedOperationalEpoch)
                {
                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        ApplicationError.Conflict(
                            "Runtime.StationControllerCommandOperationalFenceChanged",
                            "Controller operational state changed before the "
                            + "pending command was first claimed."));
                }
            }

            var continuity = await ValidateControllerCommandContinuityAsync(
                    stationId,
                    pending,
                    cancellationToken)
                .ConfigureAwait(false);
            if (continuity is not null)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(continuity);
            }

            var claimed = entry.Station.ClaimControllerCommandDelivery(
                pending.CommandId,
                ownerAgentId,
                ownerAgentInstanceId,
                fencingToken,
                clock.UtcNow);
            if (!claimed.Succeeded)
            {
                if (string.Equals(
                        claimed.Code,
                        "Runtime.StationControllerCommandExpired",
                        StringComparison.Ordinal))
                {
                    _ = await ExpireControllerCommandAsync(
                            stationId,
                            pending.CommandId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(claimed.Code, claimed.Message));
            }

            try
            {
                var revision = await repository.SaveAsync(
                        entry.Station,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                var postCommit = await agentControlLeaseValidator.ValidateAsync(
                        stationId,
                        ownerAgentId,
                        ownerAgentInstanceId,
                        fencingToken,
                        leaseHandle,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!postCommit.IsValid)
                {
                    await AbortAfterControllerFenceLossAsync(
                            stationId,
                            pending,
                            "Agent control lease changed while the controller "
                            + "command delivery was being claimed.",
                            cancellationToken)
                        .ConfigureAwait(false);
                    return Result.Failure<StationLifecyclePersistenceEntry>(
                        ApplicationError.Conflict(
                            "Runtime.StationControllerCommandFenceLost",
                            "Agent control lease changed while the controller "
                            + "command delivery was being claimed."));
                }

                if (clock.UtcNow >= pending.DeadlineUtc)
                {
                    _ = await ExpireControllerCommandAsync(
                            stationId,
                            pending.CommandId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var afterExpiry = await repository.GetByIdAsync(
                            stationId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return afterExpiry is null
                        ? NotFound(stationId)
                        : Result.Success(afterExpiry);
                }

                return Result.Success(new StationLifecyclePersistenceEntry(
                    entry.Station,
                    revision));
            }
            catch (StationLifecycleConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
            catch (StationLifecycleConcurrencyException)
            {
                return LifecycleConcurrencyConflict(stationId);
            }
        }

        return LifecycleConcurrencyConflict(stationId);
    }

    public async ValueTask<int> ExpireControllerCommandsAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await repository.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var candidate in entries.Where(static entry =>
                     entry.Station.State is not StationState.Stopped
                         and not StationState.Aborting
                         and not StationState.Aborted))
        {
            var fault = await ControllerFaultReasonAsync(
                    candidate.Station,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fault is not null)
            {
                _ = await RequireRecoveryAfterUncommandedFenceLossAsync(
                        candidate.Station.Id,
                        fault,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        entries = await repository.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var recovery in entries
                     .Where(static entry =>
                         entry.Station.PendingControllerRecovery is not null))
        {
            _ = await SynchronizeControllerRecoveryAsync(
                    recovery.Station.Id,
                    recovery.Station.PendingControllerRecovery!.IntentId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var expiredCount = 0;
        foreach (var candidate in entries)
        {
            if (candidate.Station.PendingControllerCommand is not { } pending
                || clock.UtcNow < pending.DeadlineUtc)
            {
                continue;
            }

            if (await ExpireControllerCommandAsync(
                    candidate.Station.Id,
                    pending.CommandId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                expiredCount++;
            }
        }

        return expiredCount;
    }

    private async ValueTask<string?> ControllerFaultReasonAsync(
        StationLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var handshake = await controllerHandshakeRepository.GetByIdAsync(
                lifecycle.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (handshake is null)
        {
            return "An active Station lost its controller handshake; lifecycle "
                + "abort and explicit recovery are required.";
        }

        var state = handshake.State;
        var report = state.LatestReport;
        var nowUtc = clock.UtcNow;
        if (nowUtc < report.ReceivedAtUtc
            || nowUtc - report.ReceivedAtUtc
                > controllerHandshakeOptions.TimeToLive)
        {
            return "Controller heartbeat expired while the Station lifecycle "
                + "was active.";
        }

        var lease = await agentControlLeaseRepository.ValidateGenerationAsync(
                lifecycle.Id,
                report.OwnerAgentId,
                report.OwnerAgentInstanceId,
                report.AgentFencingToken,
                cancellationToken)
            .ConfigureAwait(false);
        if (!lease.IsValid)
        {
            return "Controller reporting ownership no longer matches the active "
                + "Station Agent control lease.";
        }

        if (state.RecoveryRequired)
        {
            return "Controller handshake requires explicit recovery while the "
                + "Station lifecycle is active.";
        }

        if (report.Error)
        {
            return $"Controller reported error {report.ErrorCode} while the "
                + "Station lifecycle was active.";
        }

        if (!report.SafetyPermitGranted)
        {
            return "Controller safety permit was denied while the Station "
                + "lifecycle was active.";
        }

        if (report.ObservedMode != lifecycle.Mode)
        {
            return "Controller observed mode diverged from the active Station "
                + "lifecycle.";
        }

        if (lifecycle.PendingControllerCommand is null
            && report.ObservedState != lifecycle.State)
        {
            return "Controller observed state diverged from the active Station "
                + "lifecycle.";
        }

        return null;
    }

    private async ValueTask<ControllerCommandPreparation>
        PrepareControllerCommandAsync(
            StationId stationId,
            StationLifecycleCommand command,
            StationMode expectedMode,
            DateTimeOffset issuedAtUtc,
            CancellationToken cancellationToken)
    {
        var leaseObservation = await agentControlLeaseRepository.GetAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!leaseObservation.IsActive || leaseObservation.Lease is null)
        {
            return ControllerCommandPreparation.Rejected(
                ApplicationError.Conflict(
                    "Runtime.StationAgentControlLeaseRequired",
                    $"Station {stationId} requires an active Agent control lease "
                    + $"before {command} can be issued."));
        }

        var lease = leaseObservation.Lease;
        var entry = await controllerHandshakeRepository.GetByIdAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return ControllerCommandPreparation.Rejected(
                ApplicationError.Conflict(
                    "Runtime.StationControllerHandshakeNotReported",
                    $"Station {stationId} cannot execute {command} until its "
                    + "controller handshake has been reported."));
        }

        var reportedLease = entry.State.LatestReport;
        if (!string.Equals(
                reportedLease.OwnerAgentId,
                lease.OwnerAgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                reportedLease.OwnerAgentInstanceId,
                lease.OwnerInstanceId,
                StringComparison.Ordinal)
            || reportedLease.AgentFencingToken != lease.FencingToken)
        {
            return ControllerCommandPreparation.Rejected(
                ApplicationError.Conflict(
                    "Runtime.StationControllerHandshakeAgentLeaseMismatch",
                    "The latest controller handshake was not reported by the "
                    + "current Agent control lease generation."));
        }

        var readiness = StationControllerHandshakeReadinessEvaluator.Evaluate(
            entry.State,
            clock.UtcNow,
            controllerHandshakeOptions,
            requireRecipeConfirmation: command == StationLifecycleCommand.Start);
        if (!readiness.Allowed
            && (!IsProtectiveCommand(command)
                || !ProtectiveChannelIsUsable(
                    entry.State,
                    clock.UtcNow,
                    controllerHandshakeOptions)))
        {
            return ControllerCommandPreparation.Rejected(
                ApplicationError.Conflict(readiness.Code, readiness.Reason));
        }

        var report = entry.State.LatestReport;
        StationRecipeStartAuthority? recipeAuthority = null;
        if (command == StationLifecycleCommand.Start)
        {
            var recipeDecision = await recipeStartAuthority.ResolveAsync(
                    stationId,
                    issuedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!recipeDecision.Allowed || recipeDecision.Authority is null)
            {
                return ControllerCommandPreparation.Rejected(
                    ApplicationError.Conflict(
                        recipeDecision.Code,
                        recipeDecision.Reason));
            }

            recipeAuthority = recipeDecision.Authority;
            if (!report.RecipeConfirmed
                || !string.Equals(
                    report.ConfirmedRecipeId,
                    recipeAuthority.RecipeId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    report.ConfirmedRecipeVersion,
                    recipeAuthority.RecipeVersion,
                    StringComparison.Ordinal))
            {
                return ControllerCommandPreparation.Rejected(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerRecipeAuthorityMismatch",
                        "Controller recipe confirmation does not exactly match "
                        + "the independently released assignment, deployment, "
                        + "readback, and changeover authority."));
            }
        }

        long expectedSequence;
        try
        {
            expectedSequence = checked(report.CommandSequence + 1);
        }
        catch (OverflowException)
        {
            return ControllerCommandPreparation.Rejected(
                ApplicationError.Conflict(
                    "Runtime.StationControllerCommandSequenceExhausted",
                    "Controller command sequence is exhausted; establish a new "
                    + "controller session before issuing another command."));
        }

        return ControllerCommandPreparation.Accepted(
            new StationControllerCommandExpectation(
                CreateControllerCommandId(
                    stationId,
                    report.ControllerSessionId,
                    expectedSequence,
                    lease.OwnerAgentId,
                    lease.OwnerInstanceId,
                    lease.FencingToken,
                    entry.State.OperationalEpoch,
                    command),
                report.ControllerSessionId,
                expectedSequence,
                lease.OwnerAgentId,
                lease.OwnerInstanceId,
                lease.FencingToken,
                TriggerFor(command),
                expectedMode,
                CompletionStateFor(command),
                IdempotencyFor(command),
                SafetyClassFor(command),
                recipeAuthority?.RecipeId,
                recipeAuthority?.RecipeVersion,
                issuedAtUtc,
                issuedAtUtc.Add(controllerHandshakeOptions.CommandTimeout),
                entry.State.OperationalEpoch,
                recipeAuthority?.AssignmentId,
                recipeAuthority?.DeploymentId,
                recipeAuthority?.ConfigurationSha256));
    }

    private async ValueTask<ApplicationError?>
        ValidateControllerCommandContinuityAsync(
            StationId stationId,
            StationControllerCommandExpectation expectation,
            CancellationToken cancellationToken)
    {
        var entry = await controllerHandshakeRepository.GetByIdAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return ControllerFenceError(
                "Runtime.StationControllerHandshakeNotFound",
                "Controller handshake disappeared while the Station command "
                + "was being issued.");
        }

        var state = entry.State;
        var report = state.LatestReport;
        var nowUtc = clock.UtcNow;
        var leaseValidation = await agentControlLeaseRepository
            .ValidateGenerationAsync(
                stationId,
                expectation.OwnerAgentId,
                expectation.OwnerAgentInstanceId,
                expectation.FencingToken,
                cancellationToken)
            .ConfigureAwait(false);
        if (!leaseValidation.IsValid)
        {
            return ControllerFenceError(
                "Runtime.StationAgentControlLeaseInvalid",
                "The Agent control lease changed or expired while the Station "
                + "command was pending.");
        }

        if (!string.Equals(
                report.OwnerAgentId,
                expectation.OwnerAgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                report.OwnerAgentInstanceId,
                expectation.OwnerAgentInstanceId,
                StringComparison.Ordinal)
            || report.AgentFencingToken != expectation.FencingToken)
        {
            return ControllerFenceError(
                "Runtime.StationControllerHandshakeAgentLeaseMismatch",
                "Controller handshake ownership no longer matches the pending "
                + "Agent lease generation.");
        }

        if (nowUtc < report.ReceivedAtUtc
            || nowUtc - report.ReceivedAtUtc
                > controllerHandshakeOptions.TimeToLive)
        {
            return ControllerFenceError(
                "Runtime.StationControllerHandshakeStale",
                "Controller heartbeat became stale while the Station command "
                + "was being issued.");
        }

        var completedByDeadline =
            report.ReceivedAtUtc <= expectation.DeadlineUtc
            && report.CommandSequence == expectation.ExpectedCommandSequence
            && report.AcknowledgedCommandSequence
                == expectation.ExpectedCommandSequence
            && string.Equals(
                report.CommandId,
                expectation.CommandId,
                StringComparison.Ordinal)
            && report.CommandFencingToken == expectation.FencingToken
            && !report.Busy
            && report.Completed
            && report.ObservedMode == expectation.ExpectedMode
            && report.ObservedState == expectation.ExpectedCompletionState;
        if (nowUtc >= expectation.DeadlineUtc && !completedByDeadline)
        {
            return ControllerFenceError(
                "Runtime.StationControllerCommandExpired",
                $"Controller command {expectation.CommandId} expired at "
                + $"{expectation.DeadlineUtc:O}.");
        }

        if ((report.ReceivedAtUtc - report.SourceTimestampUtc).Duration()
            > controllerHandshakeOptions.MaximumSourceClockSkew)
        {
            return ControllerFenceError(
                "Runtime.StationControllerHandshakeClockSkew",
                "Controller source clock left the configured skew boundary.");
        }

        var protective = expectation.Trigger is
            StationTransitionTrigger.Stop or StationTransitionTrigger.Abort;
        if ((!protective
                && (state.RecoveryRequired
                    || report.Error
                    || !report.SafetyPermitGranted))
            || !string.Equals(
                report.ControllerSessionId,
                expectation.ControllerSessionId,
                StringComparison.Ordinal))
        {
            return ControllerFenceError(
                "Runtime.StationControllerCommandFenceChanged",
                "Controller session, recovery, error, or safety state changed "
                + "while the Station command was being issued.");
        }

        var priorSequence = expectation.ExpectedCommandSequence - 1;
        if (report.CommandSequence < priorSequence
            || report.CommandSequence > expectation.ExpectedCommandSequence
            || report.AcknowledgedCommandSequence
                > expectation.ExpectedCommandSequence)
        {
            return ControllerFenceError(
                "Runtime.StationControllerCommandSequenceChanged",
                $"Controller command state "
                + $"{report.CommandSequence}/{report.AcknowledgedCommandSequence} "
                + $"does not match pending sequence "
                + $"{expectation.ExpectedCommandSequence}.");
        }

        if (expectation.Trigger == StationTransitionTrigger.Start
            && (!report.RecipeConfirmed
                || !string.Equals(
                    report.ConfirmedRecipeId,
                    expectation.ConfirmedRecipeId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    report.ConfirmedRecipeVersion,
                    expectation.ConfirmedRecipeVersion,
                    StringComparison.Ordinal)))
        {
            return ControllerFenceError(
                "Runtime.StationControllerCommandRecipeChanged",
                "Controller recipe confirmation changed while Start was being issued.");
        }

        if (expectation.Trigger == StationTransitionTrigger.Start)
        {
            var recipeDecision = await recipeStartAuthority.ResolveAsync(
                    stationId,
                    nowUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            var authority = recipeDecision.Authority;
            if (!recipeDecision.Allowed
                || authority is null
                || !string.Equals(
                    authority.RecipeId,
                    expectation.ConfirmedRecipeId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    authority.RecipeVersion,
                    expectation.ConfirmedRecipeVersion,
                    StringComparison.Ordinal)
                || authority.AssignmentId != expectation.RecipeAssignmentId
                || authority.DeploymentId != expectation.RecipeDeploymentId
                || !string.Equals(
                    authority.ConfigurationSha256,
                    expectation.RecipeConfigurationSha256,
                    StringComparison.Ordinal))
            {
                return ControllerFenceError(
                    "Runtime.StationControllerRecipeAuthorityChanged",
                    "The immutable recipe assignment, deployment, readback, or "
                    + "changeover authority changed while Start was pending.");
            }
        }

        return null;
    }

    private async ValueTask<ApplicationError?>
        ValidateControllerCommandCompletionAsync(
            StationId stationId,
            StationControllerCommandExpectation expectation,
            string commandId,
            string controllerSessionId,
            long commandSequence,
            StationMode observedMode,
            StationState observedState,
            long stateSequence,
            CancellationToken cancellationToken)
    {
        if (!string.Equals(
                expectation.CommandId,
                commandId,
                StringComparison.Ordinal)
            || !string.Equals(
                expectation.ControllerSessionId,
                controllerSessionId,
                StringComparison.Ordinal)
            || expectation.ExpectedCommandSequence != commandSequence)
        {
            return ControllerFenceError(
                "Runtime.StationControllerCommandFenceMismatch",
                $"Controller acknowledgement {controllerSessionId}/{commandSequence} "
                + $"does not match pending command "
                + $"{expectation.ControllerSessionId}/"
                + $"{expectation.ExpectedCommandSequence}.");
        }

        var continuity = await ValidateControllerCommandContinuityAsync(
                stationId,
                expectation,
                cancellationToken)
            .ConfigureAwait(false);
        if (continuity is not null)
        {
            return continuity;
        }

        var entry = await controllerHandshakeRepository.GetByIdAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return ControllerFenceError(
                "Runtime.StationControllerHandshakeNotFound",
                "Controller handshake disappeared before command completion.");
        }

        var report = entry.State.LatestReport;
        if (report.CommandSequence != expectation.ExpectedCommandSequence
            || report.AcknowledgedCommandSequence
                != expectation.ExpectedCommandSequence
            || !string.Equals(
                report.CommandId,
                expectation.CommandId,
                StringComparison.Ordinal)
            || report.CommandFencingToken != expectation.FencingToken
            || report.Busy
            || !report.Completed
            || report.ObservedMode != expectation.ExpectedMode
            || report.ObservedState != expectation.ExpectedCompletionState
            || report.ObservedMode != observedMode
            || report.ObservedState != observedState
            || report.StateSequence != stateSequence
            || report.ReceivedAtUtc > expectation.DeadlineUtc)
        {
            return ControllerFenceError(
                "Runtime.StationControllerCommandIncomplete",
                $"Controller command {expectation.ExpectedCommandSequence} has "
                + "not reached its correlated completed state before the deadline.");
        }

        return null;
    }

    private async ValueTask<ApplicationError?>
        ValidateUncommandedTransitionCompletionAsync(
            StationId stationId,
            StationLifecycle lifecycle,
            string ownerAgentId,
            string ownerAgentInstanceId,
            long agentFencingToken,
            string? commandId,
            string controllerSessionId,
            long commandSequence,
            StationMode observedMode,
            StationState observedState,
            long stateSequence,
            CancellationToken cancellationToken)
    {
        var expectedState = CompletionStateFor(lifecycle.State);
        var entry = await controllerHandshakeRepository.GetByIdAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return ControllerFenceError(
                "Runtime.StationControllerHandshakeNotFound",
                "Controller handshake evidence is required before the Station "
                + "transition can be acknowledged.");
        }

        var report = entry.State.LatestReport;
        var transition = lifecycle.TransitionAudit.Count == 0
            ? null
            : lifecycle.TransitionAudit[^1];
        var nowUtc = clock.UtcNow;
        if (!string.Equals(
                report.OwnerAgentId,
                ownerAgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                report.OwnerAgentInstanceId,
                ownerAgentInstanceId,
                StringComparison.Ordinal)
            || report.AgentFencingToken != agentFencingToken
            || !string.Equals(
                report.ControllerSessionId,
                controllerSessionId,
                StringComparison.Ordinal)
            || !string.Equals(report.CommandId, commandId, StringComparison.Ordinal)
            || report.CommandSequence != commandSequence
            || report.AcknowledgedCommandSequence != commandSequence
            || report.ObservedMode != lifecycle.Mode
            || report.ObservedMode != observedMode
            || report.ObservedState != expectedState
            || report.ObservedState != observedState
            || report.StateSequence != stateSequence
            || report.Busy
            || transition is null
            || report.ReceivedAtUtc < transition.OccurredAtUtc
            || nowUtc < report.ReceivedAtUtc
            || nowUtc - report.ReceivedAtUtc
                > controllerHandshakeOptions.TimeToLive
            || (report.ReceivedAtUtc - report.SourceTimestampUtc).Duration()
                > controllerHandshakeOptions.MaximumSourceClockSkew)
        {
            return ControllerFenceError(
                "Runtime.StationControllerStateEvidenceMismatch",
                "The supplied controller state evidence does not exactly match "
                + "the current leased controller report and expected Station "
                + "completion state.");
        }

        return null;
    }

    private async ValueTask<ApplicationError?>
        ValidateCommittedUncommandedCompletionAsync(
            StationId stationId,
            StationLifecycle lifecycle,
            string ownerAgentId,
            string ownerAgentInstanceId,
            long agentFencingToken,
            string? commandId,
            string controllerSessionId,
            long commandSequence,
            StationMode observedMode,
            StationState observedState,
            long stateSequence,
            CancellationToken cancellationToken)
    {
        var entry = await controllerHandshakeRepository.GetByIdAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return ControllerFenceError(
                "Runtime.StationControllerHandshakeNotFound",
                "Controller handshake evidence disappeared after the Station "
                + "completion was committed.");
        }

        var report = entry.State.LatestReport;
        var priorTransition = lifecycle.TransitionAudit.Count >= 2
            ? lifecycle.TransitionAudit[^2]
            : null;
        var latestTransition = lifecycle.TransitionAudit.Count >= 1
            ? lifecycle.TransitionAudit[^1]
            : null;
        var nowUtc = clock.UtcNow;
        if (latestTransition?.Trigger
                is not StationTransitionTrigger.StateCompleted
            || priorTransition is null
            || report.ReceivedAtUtc < priorTransition.OccurredAtUtc
            || !string.Equals(
                report.OwnerAgentId,
                ownerAgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                report.OwnerAgentInstanceId,
                ownerAgentInstanceId,
                StringComparison.Ordinal)
            || report.AgentFencingToken != agentFencingToken
            || !string.Equals(
                report.ControllerSessionId,
                controllerSessionId,
                StringComparison.Ordinal)
            || !string.Equals(report.CommandId, commandId, StringComparison.Ordinal)
            || report.CommandSequence != commandSequence
            || report.AcknowledgedCommandSequence != commandSequence
            || report.ObservedMode != lifecycle.Mode
            || report.ObservedMode != observedMode
            || report.ObservedState != lifecycle.State
            || report.ObservedState != observedState
            || report.StateSequence != stateSequence
            || report.Busy
            || nowUtc < report.ReceivedAtUtc
            || nowUtc - report.ReceivedAtUtc
                > controllerHandshakeOptions.TimeToLive
            || (report.ReceivedAtUtc - report.SourceTimestampUtc).Duration()
                > controllerHandshakeOptions.MaximumSourceClockSkew)
        {
            return ControllerFenceError(
                "Runtime.StationControllerCompletionEvidenceChanged",
                "Controller evidence changed or became stale while the Station "
                + "completion was committed.");
        }

        return null;
    }

    private async ValueTask<bool>
        RequireRecoveryAfterUncommandedFenceLossAsync(
            StationId stationId,
            string failure,
            CancellationToken cancellationToken)
    {
        var authorization = new StationCommandAuthorization(
            "system.controller-completion-fence",
            StationCommandGrant.Abort);
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            if (entry.Station.PendingControllerRecovery is { } existing)
            {
                return await SynchronizeControllerRecoveryAsync(
                        stationId,
                        existing.IntentId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var occurredAtUtc = clock.UtcNow;
            if (entry.Station.State is not StationState.Stopped
                and not StationState.Aborting
                and not StationState.Aborted)
            {
                var abort = entry.Station.Abort(
                    authorization,
                    failure,
                    occurredAtUtc);
                if (!abort.Succeeded)
                {
                    return false;
                }
            }

            if (entry.Station.PendingControllerRecovery is null)
            {
                var canonical =
                    $"{stationId.Value}\n{entry.Revision}\n"
                    + $"{entry.Station.State}\n{failure}";
                var commandId = "uncommanded-completion-fence-"
                    + Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                        .ToLowerInvariant();
                var recovery = entry.Station.RequireControllerRecovery(
                    new StationControllerRecoveryIntent(
                        $"controller-recovery-{commandId}",
                        commandId,
                        StationControllerCommandIdempotency.NonIdempotent,
                        failure,
                        occurredAtUtc));
                if (!recovery.Succeeded)
                {
                    return false;
                }
            }

            try
            {
                _ = await repository.SaveAsync(
                        entry.Station,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await SynchronizeControllerRecoveryAsync(
                        stationId,
                        entry.Station.PendingControllerRecovery!.IntentId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (StationLifecycleConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
        }

        return false;
    }

    private static StationState CompletionStateFor(StationState state) =>
        state switch
        {
            StationState.Resetting => StationState.Idle,
            StationState.Starting => StationState.Execute,
            StationState.Completing => StationState.Complete,
            StationState.Holding => StationState.Held,
            StationState.Unholding => StationState.Execute,
            StationState.Suspending => StationState.Suspended,
            StationState.Unsuspending => StationState.Execute,
            StationState.Stopping => StationState.Stopped,
            StationState.Aborting => StationState.Aborted,
            StationState.Clearing => StationState.Stopped,
            _ => throw new InvalidOperationException(
                $"Station state {state} cannot be acknowledged as completed.")
        };

    private async ValueTask<bool> AbortAfterControllerFenceLossAsync(
        StationId stationId,
        StationControllerCommandExpectation expectation,
        string fenceFailure,
        CancellationToken cancellationToken)
    {
        var authorization = new StationCommandAuthorization(
            "system.controller-handshake-fence",
            StationCommandGrant.Abort);
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            if (entry.Station.PendingControllerRecovery is { } existing)
            {
                if (!string.Equals(
                        existing.CommandId,
                        expectation.CommandId,
                        StringComparison.Ordinal))
                {
                    _ = await SynchronizeControllerRecoveryAsync(
                            stationId,
                            existing.IntentId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                _ = await SynchronizeControllerRecoveryAsync(
                        stationId,
                        existing.IntentId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            if (entry.Station.PendingControllerCommand is { } current
                && !string.Equals(
                    current.CommandId,
                    expectation.CommandId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var requiredAtUtc = clock.UtcNow;
            var reason = $"Controller command fence lost: {fenceFailure}";
            if (entry.Station.State is not StationState.Stopped
                and not StationState.Stopping
                and not StationState.Aborting
                and not StationState.Aborted)
            {
                var abort = entry.Station.Abort(
                    authorization,
                    reason,
                    requiredAtUtc);
                if (!abort.Succeeded)
                {
                    return false;
                }
            }

            var intent = entry.Station.PendingControllerRecovery
                ?? new StationControllerRecoveryIntent(
                    $"controller-recovery-{expectation.CommandId}",
                    expectation.CommandId,
                    expectation.Idempotency,
                    reason,
                    requiredAtUtc);
            if (entry.Station.PendingControllerRecovery is null)
            {
                var recovery = entry.Station.RequireControllerRecovery(intent);
                if (!recovery.Succeeded)
                {
                    return false;
                }
            }

            try
            {
                _ = await repository.SaveAsync(
                        entry.Station,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                _ = await SynchronizeControllerRecoveryAsync(
                        stationId,
                        intent.IntentId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (StationLifecycleConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
        }

        return false;
    }

    private async ValueTask<bool> ExpireControllerCommandAsync(
        StationId stationId,
        string commandId,
        CancellationToken cancellationToken)
    {
        var entry = await repository.GetByIdAsync(stationId, cancellationToken)
            .ConfigureAwait(false);
        var pending = entry?.Station.PendingControllerCommand;
        if (entry is null
            || pending is null
            || !string.Equals(
                pending.CommandId,
                commandId,
                StringComparison.Ordinal)
            || clock.UtcNow < pending.DeadlineUtc)
        {
            return false;
        }

        if (!DeliveryClaimMatches(
                entry.Station.PendingControllerCommandDelivery,
                pending))
        {
            return await AbortAfterControllerFenceLossAsync(
                    stationId,
                    pending,
                    "Controller completion cannot be trusted because the command "
                    + "was never durably claimed for delivery.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var handshake = await controllerHandshakeRepository.GetByIdAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        var report = handshake?.State.LatestReport;
        var completion = report is null
            ? ControllerFenceError(
                "Runtime.StationControllerHandshakeNotFound",
                "Controller handshake disappeared before command completion.")
            : await ValidateControllerCommandCompletionAsync(
                stationId,
                pending,
                pending.CommandId,
                pending.ControllerSessionId,
                pending.ExpectedCommandSequence,
                report.ObservedMode,
                report.ObservedState,
                report.StateSequence,
                cancellationToken)
            .ConfigureAwait(false);
        if (completion is null)
        {
            _ = await FinalizeControllerCompletionAsync(
                    stationId,
                    pending,
                    cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var reason =
            $"Controller command {pending.CommandId} expired at "
            + $"{pending.DeadlineUtc:O}.";
        return await AbortAfterControllerFenceLossAsync(
                stationId,
                pending,
                reason,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<bool> FinalizeControllerCompletionAsync(
        StationId stationId,
        StationControllerCommandExpectation expectation,
        CancellationToken cancellationToken)
    {
        var authorization = new StationCommandAuthorization(
            "system.controller-command-watchdog",
            StationCommandGrant.AcknowledgeTransition);
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            var pending = entry?.Station.PendingControllerCommand;
            if (entry is null
                || pending is null
                || !string.Equals(
                    pending.CommandId,
                    expectation.CommandId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!DeliveryClaimMatches(
                    entry.Station.PendingControllerCommandDelivery,
                    pending))
            {
                return false;
            }

            var handshake = await controllerHandshakeRepository.GetByIdAsync(
                    stationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (handshake is null)
            {
                return false;
            }

            var report = handshake.State.LatestReport;
            if (await ValidateControllerCommandCompletionAsync(
                    stationId,
                    pending,
                    pending.CommandId,
                    pending.ControllerSessionId,
                    pending.ExpectedCommandSequence,
                    report.ObservedMode,
                    report.ObservedState,
                    report.StateSequence,
                    cancellationToken).ConfigureAwait(false) is not null)
            {
                return false;
            }

            var result = entry.Station.AcknowledgeControllerTransition(
                pending.ControllerSessionId,
                pending.ExpectedCommandSequence,
                authorization,
                "Finalize controller completion durably received before deadline.",
                clock.UtcNow);
            if (!result.Succeeded)
            {
                return false;
            }

            try
            {
                _ = await repository.SaveAsync(
                        entry.Station,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (StationLifecycleConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
        }

        return false;
    }

    private async ValueTask<bool> RequireControllerRecoveryAsync(
        StationId stationId,
        string reason,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await controllerHandshakeRepository.GetByIdAsync(
                    stationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            if (entry.State.RecoveryRequired)
            {
                return true;
            }

            var requiredAtUtc = clock.UtcNow;
            var mutation = entry.State.RequireRecovery(
                "system.controller-command-watchdog",
                reason,
                requiredAtUtc);
            if (!mutation.Succeeded || !mutation.Changed)
            {
                return mutation.Succeeded && entry.State.RecoveryRequired;
            }

            var fact = entry.State.CreateFact(
                checked(entry.Revision + 2),
                mutation.FactKind!.Value,
                "system.controller-command-watchdog",
                reason,
                requiredAtUtc);
            try
            {
                _ = await controllerHandshakeRepository.SaveAsync(
                        entry.State,
                        entry.Revision,
                        fact,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (StationControllerHandshakeConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
            catch (StationControllerHandshakeConcurrencyException)
            {
                return false;
            }
        }

        return false;
    }

    private async ValueTask<bool> SynchronizeControllerRecoveryAsync(
        StationId stationId,
        string intentId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            var recovery = entry?.Station.PendingControllerRecovery;
            if (entry is null)
            {
                return false;
            }

            if (recovery is null)
            {
                return true;
            }

            if (!string.Equals(
                    recovery.IntentId,
                    intentId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!await RequireControllerRecoveryAsync(
                    stationId,
                    recovery.Reason,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return false;
            }

            var completion = entry.Station.CompleteControllerRecoverySynchronization(
                intentId,
                clock.UtcNow);
            if (!completion.Succeeded)
            {
                return false;
            }

            try
            {
                _ = await repository.SaveAsync(
                        entry.Station,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (StationLifecycleConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
        }

        return false;
    }

    private static ApplicationError ControllerFenceError(
        string code,
        string message) =>
        ApplicationError.Conflict(code, message);

    private static string CreateControllerCommandId(
        StationId stationId,
        string controllerSessionId,
        long expectedSequence,
        string ownerAgentId,
        string ownerAgentInstanceId,
        long fencingToken,
        long operationalEpoch,
        StationLifecycleCommand command)
    {
        var canonical =
            $"{stationId.Value}\n{controllerSessionId}\n{expectedSequence}\n"
            + $"{ownerAgentId}\n{ownerAgentInstanceId}\n{fencingToken}\n{command}";
        canonical += $"\n{operationalEpoch}";
        return "station-command-"
            + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
    }

    private static Result<StationLifecyclePersistenceEntry>
        LifecycleConcurrencyConflict(StationId stationId) =>
        Result.Failure<StationLifecyclePersistenceEntry>(
            ApplicationError.Conflict(
                "Runtime.StationLifecycleConcurrencyConflict",
                $"Station lifecycle {stationId} kept changing while the command "
                + "was being applied; retry the command."));

    private async ValueTask<Result<StationLifecyclePersistenceEntry>> MutateAsync(
        StationId stationId,
        string actorId,
        string reason,
        StationCommandGrant grant,
        Func<
            StationLifecycle,
            StationCommandAuthorization,
            DateTimeOffset,
            RuntimeOperationResult> mutation,
        CancellationToken cancellationToken,
        StationLifecycleCommand? lifecycleCommand = null)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(mutation);
        _ = Required(reason, nameof(reason));
        var authorization = new StationCommandAuthorization(actorId, grant);

        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return NotFound(stationId);
            }

            if (entry.Station.PendingControllerRecovery is not null
                && lifecycleCommand is not StationLifecycleCommand.Stop
                    and not StationLifecycleCommand.Abort)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerRecoverySynchronizationPending",
                        "Controller recovery is pending durable synchronization; "
                        + "new Station activity is blocked."));
            }

            var occurredAtUtc = clock.UtcNow;
            var result = mutation(entry.Station, authorization, occurredAtUtc);
            if (!result.Succeeded)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(result.Code, result.Message));
            }

            StationControllerCommandExpectation? controllerCommand = null;
            if (lifecycleCommand is { } commanded
                && RequiresControllerCommand(commanded)
                && entry.Station.PendingControllerRecovery is null)
            {
                var preparation = await PrepareControllerCommandAsync(
                        stationId,
                        commanded,
                        entry.Station.Mode,
                        occurredAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (preparation.Error is not null)
                {
                    if (!IsProtectiveCommand(commanded))
                    {
                        return Result.Failure<StationLifecyclePersistenceEntry>(
                            preparation.Error);
                    }

                    var recovery = entry.Station.RequireControllerRecovery(
                        CreateProtectiveCommandRecoveryIntent(
                            stationId,
                            commanded,
                            preparation.Error.Message,
                            occurredAtUtc));
                    if (!recovery.Succeeded)
                    {
                        return Result.Failure<StationLifecyclePersistenceEntry>(
                            ApplicationError.Conflict(
                                recovery.Code,
                                recovery.Message));
                    }
                }
                else
                {
                    controllerCommand = preparation.Expectation!;
                    var binding = entry.Station.BindControllerCommand(
                        controllerCommand);
                    if (!binding.Succeeded)
                    {
                        return Result.Failure<StationLifecyclePersistenceEntry>(
                            ApplicationError.Conflict(
                                binding.Code,
                                binding.Message));
                    }
                }
            }

            try
            {
                var nextRevision = await repository.SaveAsync(
                        entry.Station,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                var persisted = new StationLifecyclePersistenceEntry(
                    entry.Station,
                    nextRevision);
                if (entry.Station.PendingControllerRecovery is { } recovery)
                {
                    if (await SynchronizeControllerRecoveryAsync(
                            stationId,
                            recovery.IntentId,
                            cancellationToken)
                        .ConfigureAwait(false))
                    {
                        var synchronized = await repository.GetByIdAsync(
                                stationId,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (synchronized is not null)
                        {
                            persisted = synchronized;
                        }
                    }
                }

                if (controllerCommand is null)
                {
                    return Result.Success(persisted);
                }

                var postCommit = await ValidateControllerCommandContinuityAsync(
                        stationId,
                        controllerCommand,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (postCommit is null)
                {
                    return Result.Success(persisted);
                }

                await AbortAfterControllerFenceLossAsync(
                        stationId,
                        controllerCommand,
                        postCommit.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerCommandFenceLost",
                        postCommit.Message));
            }
            catch (StationLifecycleConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
            catch (StationLifecycleConcurrencyException)
            {
                return LifecycleConcurrencyConflict(stationId);
            }
        }

        return LifecycleConcurrencyConflict(stationId);
    }

    private static RuntimeOperationResult Execute(
        StationLifecycle station,
        StationLifecycleCommand command,
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset occurredAtUtc)
    {
        return command switch
        {
            StationLifecycleCommand.Reset =>
                station.Reset(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Start =>
                station.Start(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Complete =>
                station.Complete(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Hold =>
                station.Hold(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Unhold =>
                station.Unhold(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Suspend =>
                station.Suspend(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Unsuspend =>
                station.Unsuspend(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Stop =>
                station.Stop(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Abort =>
                station.Abort(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Clear =>
                station.Clear(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Acknowledge =>
                station.AcknowledgeTransition(authorization, reason, occurredAtUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
    }

    private static StationCommandGrant GrantFor(StationLifecycleCommand command)
    {
        return command switch
        {
            StationLifecycleCommand.Reset => StationCommandGrant.Reset,
            StationLifecycleCommand.Start => StationCommandGrant.Start,
            StationLifecycleCommand.Complete => StationCommandGrant.Complete,
            StationLifecycleCommand.Hold => StationCommandGrant.Hold,
            StationLifecycleCommand.Unhold => StationCommandGrant.Unhold,
            StationLifecycleCommand.Suspend => StationCommandGrant.Suspend,
            StationLifecycleCommand.Unsuspend => StationCommandGrant.Unsuspend,
            StationLifecycleCommand.Stop => StationCommandGrant.Stop,
            StationLifecycleCommand.Abort => StationCommandGrant.Abort,
            StationLifecycleCommand.Clear => StationCommandGrant.Clear,
            StationLifecycleCommand.Acknowledge =>
                StationCommandGrant.AcknowledgeTransition,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
    }

    private static bool RequiresControllerCommand(
        StationLifecycleCommand command) =>
        command is not StationLifecycleCommand.Acknowledge;

    private static bool DeliveryClaimMatches(
        StationControllerCommandDeliveryClaim? claim,
        StationControllerCommandExpectation command) =>
        claim is not null
        && string.Equals(claim.CommandId, command.CommandId, StringComparison.Ordinal)
        && string.Equals(
            claim.OwnerAgentId,
            command.OwnerAgentId,
            StringComparison.Ordinal)
        && string.Equals(
            claim.OwnerAgentInstanceId,
            command.OwnerAgentInstanceId,
            StringComparison.Ordinal)
        && claim.FencingToken == command.FencingToken;

    private static bool IsProtectiveCommand(
        StationLifecycleCommand command) =>
        command is StationLifecycleCommand.Stop
            or StationLifecycleCommand.Abort;

    private static bool ProtectiveChannelIsUsable(
        StationControllerHandshake state,
        DateTimeOffset nowUtc,
        StationControllerHandshakeOptions options)
    {
        var report = state.LatestReport;
        return nowUtc >= report.ReceivedAtUtc
            && nowUtc - report.ReceivedAtUtc <= options.TimeToLive
            && (report.ReceivedAtUtc - report.SourceTimestampUtc).Duration()
                <= options.MaximumSourceClockSkew;
    }

    private static StationControllerRecoveryIntent
        CreateProtectiveCommandRecoveryIntent(
            StationId stationId,
            StationLifecycleCommand command,
            string failure,
            DateTimeOffset occurredAtUtc)
    {
        var canonical =
            $"{stationId.Value}\n{command}\n{occurredAtUtc:O}\n{failure}";
        var commandId = "protective-command-unavailable-"
            + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        return new StationControllerRecoveryIntent(
            $"controller-recovery-{commandId}",
            commandId,
            StationControllerCommandIdempotency.Idempotent,
            $"Protective {command} was requested in lifecycle state, but no "
                + $"current fenced controller channel was available: {failure}",
            occurredAtUtc);
    }

    private static StationTransitionTrigger TriggerFor(
        StationLifecycleCommand command) =>
        command switch
        {
            StationLifecycleCommand.Reset => StationTransitionTrigger.Reset,
            StationLifecycleCommand.Start => StationTransitionTrigger.Start,
            StationLifecycleCommand.Complete => StationTransitionTrigger.Complete,
            StationLifecycleCommand.Hold => StationTransitionTrigger.Hold,
            StationLifecycleCommand.Unhold => StationTransitionTrigger.Unhold,
            StationLifecycleCommand.Suspend => StationTransitionTrigger.Suspend,
            StationLifecycleCommand.Unsuspend => StationTransitionTrigger.Unsuspend,
            StationLifecycleCommand.Stop => StationTransitionTrigger.Stop,
            StationLifecycleCommand.Abort => StationTransitionTrigger.Abort,
            StationLifecycleCommand.Clear => StationTransitionTrigger.Clear,
            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                "Acknowledgement does not create a controller command.")
        };

    private static StationState CompletionStateFor(
        StationLifecycleCommand command) =>
        command switch
        {
            StationLifecycleCommand.Reset => StationState.Idle,
            StationLifecycleCommand.Start => StationState.Execute,
            StationLifecycleCommand.Complete => StationState.Complete,
            StationLifecycleCommand.Hold => StationState.Held,
            StationLifecycleCommand.Unhold => StationState.Execute,
            StationLifecycleCommand.Suspend => StationState.Suspended,
            StationLifecycleCommand.Unsuspend => StationState.Execute,
            StationLifecycleCommand.Stop => StationState.Stopped,
            StationLifecycleCommand.Abort => StationState.Aborted,
            StationLifecycleCommand.Clear => StationState.Stopped,
            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                "Acknowledgement has no controller completion state.")
        };

    private static StationControllerCommandIdempotency IdempotencyFor(
        StationLifecycleCommand command) =>
        command switch
        {
            StationLifecycleCommand.Start
                or StationLifecycleCommand.Unhold
                or StationLifecycleCommand.Unsuspend =>
                StationControllerCommandIdempotency.NonIdempotent,
            StationLifecycleCommand.Complete =>
                StationControllerCommandIdempotency.ConditionallyIdempotent,
            StationLifecycleCommand.Reset
                or StationLifecycleCommand.Hold
                or StationLifecycleCommand.Suspend
                or StationLifecycleCommand.Stop
                or StationLifecycleCommand.Abort
                or StationLifecycleCommand.Clear =>
                StationControllerCommandIdempotency.Idempotent,
            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                "Acknowledgement has no idempotency classification.")
        };

    private static StationControllerCommandSafetyClass SafetyClassFor(
        StationLifecycleCommand command) =>
        command is StationLifecycleCommand.Stop or StationLifecycleCommand.Abort
            ? StationControllerCommandSafetyClass.SafetyRelevant
            : StationControllerCommandSafetyClass.Operational;

    private static Result<StationLifecyclePersistenceEntry> NotFound(StationId stationId)
    {
        return Result.Failure<StationLifecyclePersistenceEntry>(
            ApplicationError.NotFound(
                "Runtime.StationLifecycleNotFound",
                $"Station lifecycle {stationId} was not found."));
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }

    private sealed record ControllerCommandPreparation(
        StationControllerCommandExpectation? Expectation,
        ApplicationError? Error)
    {
        public static ControllerCommandPreparation Accepted(
            StationControllerCommandExpectation expectation) =>
            new(expectation, null);

        public static ControllerCommandPreparation Rejected(
            ApplicationError error) =>
            new(null, error);
    }
}
