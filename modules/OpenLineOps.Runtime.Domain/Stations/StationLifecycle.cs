using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Domain.Stations;

public sealed record StationTransitionAuditEntry(
    long Sequence,
    StationState FromState,
    StationState ToState,
    StationTransitionTrigger Trigger,
    StationMode Mode,
    string ActorId,
    string Reason,
    StationReadiness Readiness,
    DateTimeOffset OccurredAtUtc,
    StationControllerCommandExpectation? ControllerCommand = null);

public sealed record StationControllerCommandExpectation
{
    public StationControllerCommandExpectation(
        string commandId,
        string controllerSessionId,
        long expectedCommandSequence,
        string ownerAgentId,
        string ownerAgentInstanceId,
        long fencingToken,
        StationTransitionTrigger trigger,
        StationMode expectedMode,
        StationState expectedCompletionState,
        StationControllerCommandIdempotency idempotency,
        StationControllerCommandSafetyClass safetyClass,
        string? confirmedRecipeId,
        string? confirmedRecipeVersion,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset deadlineUtc,
        long issuedOperationalEpoch = 1,
        Guid? recipeAssignmentId = null,
        Guid? recipeDeploymentId = null,
        string? recipeConfigurationSha256 = null)
    {
        CommandId = StationLifecycleGuard.Canonical(
            commandId,
            nameof(commandId));
        ControllerSessionId = StationLifecycleGuard.Canonical(
            controllerSessionId,
            nameof(controllerSessionId));
        OwnerAgentId = StationLifecycleGuard.Canonical(
            ownerAgentId,
            nameof(ownerAgentId));
        OwnerAgentInstanceId =
            StationAgentControlLease.RequireOwnerInstanceId(
                ownerAgentInstanceId,
                nameof(ownerAgentInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedCommandSequence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        if (trigger is StationTransitionTrigger.StateCompleted
            or StationTransitionTrigger.SafetyPermitLost)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trigger),
                trigger,
                "Controller command expectations require an operator command trigger.");
        }

        if (!Enum.IsDefined(expectedMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedMode),
                expectedMode,
                "Expected controller mode is invalid.");
        }

        if (!Enum.IsDefined(expectedCompletionState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedCompletionState),
                expectedCompletionState,
                "Expected controller completion state is invalid.");
        }

        if (!Enum.IsDefined(idempotency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(idempotency),
                idempotency,
                "Controller command idempotency is invalid.");
        }

        if (!Enum.IsDefined(safetyClass))
        {
            throw new ArgumentOutOfRangeException(
                nameof(safetyClass),
                safetyClass,
                "Controller command safety class is invalid.");
        }

        if ((confirmedRecipeId is null) != (confirmedRecipeVersion is null))
        {
            throw new ArgumentException(
                "Confirmed recipe identity and version must both be present or both be null.");
        }

        ConfirmedRecipeId = confirmedRecipeId is null
            ? null
            : StationLifecycleGuard.Canonical(
                confirmedRecipeId,
                nameof(confirmedRecipeId));
        ConfirmedRecipeVersion = confirmedRecipeVersion is null
            ? null
            : StationLifecycleGuard.Canonical(
                confirmedRecipeVersion,
                nameof(confirmedRecipeVersion));
        var recipeAuthorityFields =
            (confirmedRecipeId is not null ? 1 : 0)
            + (recipeAssignmentId is not null ? 1 : 0)
            + (recipeDeploymentId is not null ? 1 : 0)
            + (recipeConfigurationSha256 is not null ? 1 : 0);
        if (recipeAuthorityFields is not (0 or 4))
        {
            throw new ArgumentException(
                "Recipe identity, assignment, deployment, and configuration "
                + "authority must all be present or all be null.");
        }

        if (recipeAssignmentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Recipe assignment id cannot be empty.",
                nameof(recipeAssignmentId));
        }

        if (recipeDeploymentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Recipe deployment id cannot be empty.",
                nameof(recipeDeploymentId));
        }

        RecipeAssignmentId = recipeAssignmentId;
        RecipeDeploymentId = recipeDeploymentId;
        RecipeConfigurationSha256 = recipeConfigurationSha256 is null
            ? null
            : StationAgentControlLease.RequireProofSha256(
                recipeConfigurationSha256,
                nameof(recipeConfigurationSha256));
        StationLifecycleGuard.Utc(issuedAtUtc, nameof(issuedAtUtc));
        StationLifecycleGuard.Utc(deadlineUtc, nameof(deadlineUtc));
        if (deadlineUtc <= issuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deadlineUtc),
                deadlineUtc,
                "Controller command deadline must follow its issue time.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(
            issuedOperationalEpoch,
            1);
        ExpectedCommandSequence = expectedCommandSequence;
        FencingToken = fencingToken;
        Trigger = trigger;
        ExpectedMode = expectedMode;
        ExpectedCompletionState = expectedCompletionState;
        Idempotency = idempotency;
        SafetyClass = safetyClass;
        IssuedAtUtc = issuedAtUtc;
        DeadlineUtc = deadlineUtc;
        IssuedOperationalEpoch = issuedOperationalEpoch;
    }

    public string CommandId { get; }

    public string ControllerSessionId { get; }

    public long ExpectedCommandSequence { get; }

    public string OwnerAgentId { get; }

    public string OwnerAgentInstanceId { get; }

    public long FencingToken { get; }

    public StationTransitionTrigger Trigger { get; }

    public StationMode ExpectedMode { get; }

    public StationState ExpectedCompletionState { get; }

    public StationControllerCommandIdempotency Idempotency { get; }

    public StationControllerCommandSafetyClass SafetyClass { get; }

    public string? ConfirmedRecipeId { get; }

    public string? ConfirmedRecipeVersion { get; }

    public Guid? RecipeAssignmentId { get; }

    public Guid? RecipeDeploymentId { get; }

    public string? RecipeConfigurationSha256 { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset DeadlineUtc { get; }

    public long IssuedOperationalEpoch { get; }
}

public enum StationControllerCommandIdempotency
{
    Idempotent,
    ConditionallyIdempotent,
    NonIdempotent
}

public enum StationControllerCommandSafetyClass
{
    Operational,
    SafetyRelevant
}

public sealed record StationControllerCommandDeliveryClaim
{
    public StationControllerCommandDeliveryClaim(
        string commandId,
        string ownerAgentId,
        string ownerAgentInstanceId,
        long fencingToken,
        DateTimeOffset firstClaimedAtUtc,
        DateTimeOffset lastClaimedAtUtc,
        int deliveryCount)
    {
        CommandId = StationLifecycleGuard.Canonical(commandId, nameof(commandId));
        OwnerAgentId = StationLifecycleGuard.Canonical(
            ownerAgentId,
            nameof(ownerAgentId));
        OwnerAgentInstanceId =
            StationAgentControlLease.RequireOwnerInstanceId(
                ownerAgentInstanceId,
                nameof(ownerAgentInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        StationLifecycleGuard.Utc(
            firstClaimedAtUtc,
            nameof(firstClaimedAtUtc));
        StationLifecycleGuard.Utc(lastClaimedAtUtc, nameof(lastClaimedAtUtc));
        if (lastClaimedAtUtc < firstClaimedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastClaimedAtUtc),
                lastClaimedAtUtc,
                "The latest delivery claim cannot precede the first claim.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(deliveryCount, 1);
        FencingToken = fencingToken;
        FirstClaimedAtUtc = firstClaimedAtUtc;
        LastClaimedAtUtc = lastClaimedAtUtc;
        DeliveryCount = deliveryCount;
    }

    public string CommandId { get; }

    public string OwnerAgentId { get; }

    public string OwnerAgentInstanceId { get; }

    public long FencingToken { get; }

    public DateTimeOffset FirstClaimedAtUtc { get; }

    public DateTimeOffset LastClaimedAtUtc { get; }

    public int DeliveryCount { get; }
}

public sealed record StationControllerRecoveryIntent
{
    public StationControllerRecoveryIntent(
        string intentId,
        string commandId,
        StationControllerCommandIdempotency idempotency,
        string reason,
        DateTimeOffset requiredAtUtc)
    {
        IntentId = StationLifecycleGuard.Canonical(intentId, nameof(intentId));
        CommandId = StationLifecycleGuard.Canonical(commandId, nameof(commandId));
        if (!Enum.IsDefined(idempotency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(idempotency),
                idempotency,
                "Controller recovery idempotency is invalid.");
        }

        Reason = StationLifecycleGuard.Canonical(reason, nameof(reason));
        StationLifecycleGuard.Utc(requiredAtUtc, nameof(requiredAtUtc));
        Idempotency = idempotency;
        RequiredAtUtc = requiredAtUtc;
    }

    public string IntentId { get; }

    public string CommandId { get; }

    public StationControllerCommandIdempotency Idempotency { get; }

    public string Reason { get; }

    public DateTimeOffset RequiredAtUtc { get; }
}

public sealed record StationLifecycleSnapshot(
    StationId StationId,
    StationMode Mode,
    StationState State,
    StationReadiness Readiness,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    IReadOnlyList<StationTransitionAuditEntry> TransitionAudit,
    StationControllerCommandExpectation? PendingControllerCommand = null,
    StationControllerRecoveryIntent? PendingControllerRecovery = null,
    StationControllerCommandDeliveryClaim? PendingControllerCommandDelivery = null);

public sealed class StationLifecycle : AggregateRoot<StationId>
{
    private static readonly StationLifecyclePolicy Policy = new();
    private readonly List<StationTransitionAuditEntry> _transitionAudit = [];

    private StationLifecycle(
        StationId stationId,
        StationMode mode,
        StationState state,
        StationReadiness readiness,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastChangedAtUtc)
        : base(stationId)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ValidateEnum(mode, nameof(mode));
        ValidateEnum(state, nameof(state));
        ArgumentNullException.ThrowIfNull(readiness);
        StationLifecycleGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        StationLifecycleGuard.Utc(lastChangedAtUtc, nameof(lastChangedAtUtc));
        if (lastChangedAtUtc < createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastChangedAtUtc),
                lastChangedAtUtc,
                "Station last-changed time cannot precede its creation time.");
        }

        Mode = mode;
        State = state;
        Readiness = readiness;
        CreatedAtUtc = createdAtUtc;
        LastChangedAtUtc = lastChangedAtUtc;
    }

    public StationMode Mode { get; private set; }

    public StationState State { get; private set; }

    public StationReadiness Readiness { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastChangedAtUtc { get; private set; }

    public StationControllerCommandExpectation? PendingControllerCommand
    {
        get;
        private set;
    }

    public StationControllerRecoveryIntent? PendingControllerRecovery
    {
        get;
        private set;
    }

    public StationControllerCommandDeliveryClaim?
        PendingControllerCommandDelivery
    {
        get;
        private set;
    }

    public IReadOnlyList<StationTransitionAuditEntry> TransitionAudit =>
        _transitionAudit.AsReadOnly();

    public static StationLifecycle Create(
        StationId stationId,
        StationMode initialMode,
        StationReadiness initialReadiness,
        string actorId,
        DateTimeOffset createdAtUtc)
    {
        var actor = StationLifecycleGuard.Canonical(actorId, nameof(actorId));
        var station = new StationLifecycle(
            stationId,
            initialMode,
            StationState.Stopped,
            initialReadiness,
            createdAtUtc,
            createdAtUtc);
        station.RaiseDomainEvent(new StationLifecycleCreatedDomainEvent(
            stationId,
            initialMode,
            station.State,
            initialReadiness,
            actor)
        {
            OccurredAtUtc = createdAtUtc
        });
        return station;
    }

    public static StationLifecycle Restore(StationLifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.TransitionAudit);
        var station = new StationLifecycle(
            snapshot.StationId,
            snapshot.Mode,
            snapshot.State,
            snapshot.Readiness,
            snapshot.CreatedAtUtc,
            snapshot.LastChangedAtUtc);
        station._transitionAudit.AddRange(snapshot.TransitionAudit);
        station.PendingControllerCommand = snapshot.PendingControllerCommand;
        station.PendingControllerRecovery = snapshot.PendingControllerRecovery;
        station.PendingControllerCommandDelivery =
            snapshot.PendingControllerCommandDelivery;
        station.ValidateRestoredAudit();
        station.ClearDomainEvents();
        return station;
    }

    public RuntimeOperationResult ChangeMode(
        StationMode mode,
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset changedAtUtc)
    {
        ValidateEnum(mode, nameof(mode));
        var canonicalReason = StationLifecycleGuard.Canonical(reason, nameof(reason));
        var authorizationResult = Authorize(
            authorization,
            StationCommandGrant.ChangeMode);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        StationLifecycleGuard.Monotonic(
            changedAtUtc,
            LastChangedAtUtc,
            nameof(changedAtUtc));
        if (Mode == mode)
        {
            return RuntimeOperationResult.Accepted($"Station is already in {mode} mode.");
        }

        if (State != StationState.Stopped)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationModeChangeRejected",
                $"Station mode can change only while Stopped; current state is {State}.");
        }

        var from = Mode;
        Mode = mode;
        LastChangedAtUtc = changedAtUtc;
        RaiseDomainEvent(new StationModeChangedDomainEvent(
            Id,
            from,
            mode,
            authorization.ActorId,
            canonicalReason)
        {
            OccurredAtUtc = changedAtUtc
        });
        return RuntimeOperationResult.Accepted("Station mode changed.");
    }

    public RuntimeOperationResult UpdateReadiness(
        StationReadiness readiness,
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        var canonicalReason = StationLifecycleGuard.Canonical(reason, nameof(reason));
        var authorizationResult = Authorize(
            authorization,
            StationCommandGrant.ReportReadiness);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        StationLifecycleGuard.Monotonic(
            observedAtUtc,
            LastChangedAtUtc,
            nameof(observedAtUtc));
        if (Readiness == readiness)
        {
            return RuntimeOperationResult.Accepted("Station readiness is unchanged.");
        }

        var previous = Readiness;
        Readiness = readiness;
        LastChangedAtUtc = observedAtUtc;
        RaiseDomainEvent(new StationReadinessChangedDomainEvent(
            Id,
            previous,
            readiness,
            authorization.ActorId,
            canonicalReason)
        {
            OccurredAtUtc = observedAtUtc
        });

        if (previous.SafetyPermitGranted
            && !readiness.SafetyPermitGranted
            && Policy.RequiresAbortOnSafetyPermitLoss(State))
        {
            var decision = Policy.Evaluate(
                State,
                StationTransitionTrigger.SafetyPermitLost,
                readiness);
            Transition(
                decision.ToState,
                StationTransitionTrigger.SafetyPermitLost,
                authorization.ActorId,
                canonicalReason,
                observedAtUtc);
            return RuntimeOperationResult.Accepted(
                "Station readiness updated and safety permit loss initiated an abort.");
        }

        return RuntimeOperationResult.Accepted("Station readiness updated.");
    }

    public RuntimeOperationResult Reset(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Reset,
            StationCommandGrant.Reset,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Start(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Start,
            StationCommandGrant.Start,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Complete(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Complete,
            StationCommandGrant.Complete,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Hold(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Hold,
            StationCommandGrant.Hold,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Unhold(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Unhold,
            StationCommandGrant.Unhold,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Suspend(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Suspend,
            StationCommandGrant.Suspend,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Unsuspend(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Unsuspend,
            StationCommandGrant.Unsuspend,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Stop(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Stop,
            StationCommandGrant.Stop,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Abort(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Abort,
            StationCommandGrant.Abort,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult Clear(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset requestedAtUtc) =>
        Execute(
            StationTransitionTrigger.Clear,
            StationCommandGrant.Clear,
            authorization,
            reason,
            requestedAtUtc);

    public RuntimeOperationResult AcknowledgeTransition(
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset acknowledgedAtUtc)
    {
        if (PendingControllerCommand is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandFenceRequired",
                "The pending controller command must be acknowledged with its "
                + "controller session and command sequence.");
        }

        return Execute(
            StationTransitionTrigger.StateCompleted,
            StationCommandGrant.AcknowledgeTransition,
            authorization,
            reason,
            acknowledgedAtUtc);
    }

    public RuntimeOperationResult BindControllerCommand(
        StationControllerCommandExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        if (PendingControllerCommand is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandAlreadyPending",
                $"Controller command sequence "
                + $"{PendingControllerCommand.ExpectedCommandSequence} is already pending.");
        }

        if (_transitionAudit.Count == 0)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandTransitionMissing",
                "A controller command can be bound only to a persisted state transition.");
        }

        var transition = _transitionAudit[^1];
        if (transition.Trigger != expectation.Trigger
            || transition.ToState != State
            || transition.OccurredAtUtc != expectation.IssuedAtUtc
            || transition.ControllerCommand is not null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandTransitionMismatch",
                "Controller command expectation does not match the latest Station transition.");
        }

        PendingControllerCommand = expectation;
        PendingControllerCommandDelivery = null;
        _transitionAudit[^1] = transition with
        {
            ControllerCommand = expectation
        };
        RaiseDomainEvent(new StationControllerCommandIssuedDomainEvent(
            Id,
            expectation)
        {
            OccurredAtUtc = expectation.IssuedAtUtc
        });
        return RuntimeOperationResult.Accepted(
            $"Controller command sequence {expectation.ExpectedCommandSequence} is pending.");
    }

    public RuntimeOperationResult ClaimControllerCommandDelivery(
        string commandId,
        string ownerAgentId,
        string ownerAgentInstanceId,
        long fencingToken,
        DateTimeOffset claimedAtUtc)
    {
        var canonicalCommandId = StationLifecycleGuard.Canonical(
            commandId,
            nameof(commandId));
        var canonicalOwnerAgentId = StationLifecycleGuard.Canonical(
            ownerAgentId,
            nameof(ownerAgentId));
        var canonicalOwnerInstanceId =
            StationAgentControlLease.RequireOwnerInstanceId(
                ownerAgentInstanceId,
                nameof(ownerAgentInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        StationLifecycleGuard.Monotonic(
            claimedAtUtc,
            LastChangedAtUtc,
            nameof(claimedAtUtc));
        var pending = PendingControllerCommand;
        if (pending is null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandNotPending",
                "There is no pending controller command to claim.");
        }

        if (!string.Equals(
                pending.CommandId,
                canonicalCommandId,
                StringComparison.Ordinal)
            || !string.Equals(
                pending.OwnerAgentId,
                canonicalOwnerAgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                pending.OwnerAgentInstanceId,
                canonicalOwnerInstanceId,
                StringComparison.Ordinal)
            || pending.FencingToken != fencingToken)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandLeaseFenceMismatch",
                "The controller command delivery claim does not match its "
                + "Agent lease fence.");
        }

        if (claimedAtUtc >= pending.DeadlineUtc)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandExpired",
                $"Controller command {pending.CommandId} expired at "
                + $"{pending.DeadlineUtc:O} and cannot be delivered.");
        }

        var existing = PendingControllerCommandDelivery;
        if (existing is not null
            && (!string.Equals(
                    existing.CommandId,
                    canonicalCommandId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.OwnerAgentId,
                    canonicalOwnerAgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.OwnerAgentInstanceId,
                    canonicalOwnerInstanceId,
                    StringComparison.Ordinal)
                || existing.FencingToken != fencingToken))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandDeliveryClaimConflict",
                "The pending controller command was already claimed by a "
                + "different Agent lease generation.");
        }

        var next = existing is null
            ? new StationControllerCommandDeliveryClaim(
                canonicalCommandId,
                canonicalOwnerAgentId,
                canonicalOwnerInstanceId,
                fencingToken,
                claimedAtUtc,
                claimedAtUtc,
                deliveryCount: 1)
            : new StationControllerCommandDeliveryClaim(
                existing.CommandId,
                existing.OwnerAgentId,
                existing.OwnerAgentInstanceId,
                existing.FencingToken,
                existing.FirstClaimedAtUtc,
                claimedAtUtc,
                checked(existing.DeliveryCount + 1));
        PendingControllerCommandDelivery = next;
        LastChangedAtUtc = claimedAtUtc;
        RaiseDomainEvent(new StationControllerCommandDeliveryClaimedDomainEvent(
            Id,
            next)
        {
            OccurredAtUtc = claimedAtUtc
        });
        return RuntimeOperationResult.Accepted(
            $"Controller command {canonicalCommandId} delivery was claimed.");
    }

    public RuntimeOperationResult AcknowledgeControllerTransition(
        string controllerSessionId,
        long commandSequence,
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset acknowledgedAtUtc)
    {
        var sessionId = StationLifecycleGuard.Canonical(
            controllerSessionId,
            nameof(controllerSessionId));
        ArgumentOutOfRangeException.ThrowIfLessThan(commandSequence, 1);
        var pending = PendingControllerCommand;
        if (pending is null)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandNotPending",
                "The current Station transition has no pending controller command.");
        }

        if (!string.Equals(
                pending.ControllerSessionId,
                sessionId,
                StringComparison.Ordinal)
            || pending.ExpectedCommandSequence != commandSequence)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerCommandFenceMismatch",
                $"Controller acknowledgement {sessionId}/{commandSequence} does not "
                + $"match pending command {pending.ControllerSessionId}/"
                + $"{pending.ExpectedCommandSequence}.");
        }

        var result = Execute(
            StationTransitionTrigger.StateCompleted,
            StationCommandGrant.AcknowledgeTransition,
            authorization,
            reason,
            acknowledgedAtUtc);
        if (result.Succeeded)
        {
            PendingControllerCommand = null;
            PendingControllerCommandDelivery = null;
        }

        return result;
    }

    public RuntimeOperationResult RequireControllerRecovery(
        StationControllerRecoveryIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        StationLifecycleGuard.Monotonic(
            intent.RequiredAtUtc,
            LastChangedAtUtc,
            nameof(intent.RequiredAtUtc));
        if (PendingControllerRecovery is { } pending)
        {
            return string.Equals(
                    pending.IntentId,
                    intent.IntentId,
                    StringComparison.Ordinal)
                && string.Equals(
                    pending.CommandId,
                    intent.CommandId,
                    StringComparison.Ordinal)
                ? RuntimeOperationResult.Accepted(
                    $"Controller recovery intent {intent.IntentId} is already pending.")
                : RuntimeOperationResult.Rejected(
                    "Runtime.StationControllerRecoveryAlreadyPending",
                    $"Controller recovery intent {pending.IntentId} must be synchronized "
                    + "before another intent can be recorded.");
        }

        PendingControllerRecovery = intent;
        LastChangedAtUtc = intent.RequiredAtUtc;
        RaiseDomainEvent(new StationControllerRecoveryRequiredDomainEvent(
            Id,
            intent)
        {
            OccurredAtUtc = intent.RequiredAtUtc
        });
        return RuntimeOperationResult.Accepted(
            $"Controller recovery intent {intent.IntentId} is pending synchronization.");
    }

    public RuntimeOperationResult CompleteControllerRecoverySynchronization(
        string intentId,
        DateTimeOffset synchronizedAtUtc)
    {
        var canonicalIntentId = StationLifecycleGuard.Canonical(
            intentId,
            nameof(intentId));
        StationLifecycleGuard.Monotonic(
            synchronizedAtUtc,
            LastChangedAtUtc,
            nameof(synchronizedAtUtc));
        if (PendingControllerRecovery is null)
        {
            return RuntimeOperationResult.Accepted(
                $"Controller recovery intent {canonicalIntentId} is already synchronized.");
        }

        if (!string.Equals(
                PendingControllerRecovery.IntentId,
                canonicalIntentId,
                StringComparison.Ordinal))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StationControllerRecoveryIntentMismatch",
                $"Controller recovery intent {canonicalIntentId} does not match pending "
                + $"{PendingControllerRecovery.IntentId}.");
        }

        PendingControllerRecovery = null;
        LastChangedAtUtc = synchronizedAtUtc;
        RaiseDomainEvent(new StationControllerRecoverySynchronizedDomainEvent(
            Id,
            canonicalIntentId)
        {
            OccurredAtUtc = synchronizedAtUtc
        });
        return RuntimeOperationResult.Accepted(
            $"Controller recovery intent {canonicalIntentId} synchronized.");
    }

    public StationLifecycleSnapshot ToSnapshot()
    {
        return new StationLifecycleSnapshot(
            Id,
            Mode,
            State,
            Readiness,
            CreatedAtUtc,
            LastChangedAtUtc,
            TransitionAudit.ToArray(),
            PendingControllerCommand,
            PendingControllerRecovery,
            PendingControllerCommandDelivery);
    }

    private RuntimeOperationResult Execute(
        StationTransitionTrigger trigger,
        StationCommandGrant grant,
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset occurredAtUtc)
    {
        var canonicalReason = StationLifecycleGuard.Canonical(reason, nameof(reason));
        var authorizationResult = Authorize(authorization, grant);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        StationLifecycleGuard.Monotonic(
            occurredAtUtc,
            LastChangedAtUtc,
            nameof(occurredAtUtc));
        var decision = Policy.Evaluate(State, trigger, Readiness);
        if (!decision.Succeeded)
        {
            return RuntimeOperationResult.Rejected(decision.Code, decision.Message);
        }

        Transition(
            decision.ToState,
            trigger,
            authorization.ActorId,
            canonicalReason,
            occurredAtUtc);
        return RuntimeOperationResult.Accepted(decision.Message);
    }

    private static RuntimeOperationResult? Authorize(
        StationCommandAuthorization authorization,
        StationCommandGrant grant)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return authorization.Allows(grant)
            ? null
            : RuntimeOperationResult.Rejected(
                "Runtime.StationCommandUnauthorized",
                $"Actor {authorization.ActorId} is not authorized for {grant}.");
    }

    private void Transition(
        StationState targetState,
        StationTransitionTrigger trigger,
        string actorId,
        string reason,
        DateTimeOffset occurredAtUtc)
    {
        if (PendingControllerCommand is { } interruptedCommand
            && trigger != StationTransitionTrigger.StateCompleted)
        {
            if (PendingControllerRecovery is null)
            {
                var intent = new StationControllerRecoveryIntent(
                    $"controller-recovery-{interruptedCommand.CommandId}",
                    interruptedCommand.CommandId,
                    interruptedCommand.Idempotency,
                    $"Controller command {interruptedCommand.CommandId} was "
                    + $"preempted by {trigger}: {reason}",
                    occurredAtUtc);
                PendingControllerRecovery = intent;
                RaiseDomainEvent(new StationControllerRecoveryRequiredDomainEvent(
                    Id,
                    intent)
                {
                    OccurredAtUtc = occurredAtUtc
                });
            }

            PendingControllerCommand = null;
            PendingControllerCommandDelivery = null;
        }

        var transition = new StationTransitionAuditEntry(
            _transitionAudit.Count + 1L,
            State,
            targetState,
            trigger,
            Mode,
            actorId,
            reason,
            Readiness,
            occurredAtUtc);
        State = targetState;
        LastChangedAtUtc = occurredAtUtc;
        _transitionAudit.Add(transition);
        RaiseDomainEvent(new StationStateTransitionedDomainEvent(Id, transition)
        {
            OccurredAtUtc = occurredAtUtc
        });
    }

    private void ValidateRestoredAudit()
    {
        var expectedFromState = StationState.Stopped;
        var previousAtUtc = CreatedAtUtc;
        for (var index = 0; index < _transitionAudit.Count; index++)
        {
            var transition = _transitionAudit[index]
                ?? throw new InvalidOperationException(
                    "Station transition audit cannot contain null entries.");
            if (transition.Sequence != index + 1L)
            {
                throw new InvalidOperationException(
                    "Station transition audit sequence must be contiguous and one-based.");
            }

            ValidateEnum(transition.FromState, nameof(transition.FromState));
            ValidateEnum(transition.ToState, nameof(transition.ToState));
            ValidateEnum(transition.Trigger, nameof(transition.Trigger));
            ValidateEnum(transition.Mode, nameof(transition.Mode));
            _ = StationLifecycleGuard.Canonical(
                transition.ActorId,
                nameof(transition.ActorId));
            _ = StationLifecycleGuard.Canonical(
                transition.Reason,
                nameof(transition.Reason));
            ArgumentNullException.ThrowIfNull(transition.Readiness);
            StationLifecycleGuard.Monotonic(
                transition.OccurredAtUtc,
                previousAtUtc,
                nameof(transition.OccurredAtUtc));
            if (transition.OccurredAtUtc > LastChangedAtUtc)
            {
                throw new InvalidOperationException(
                    "Station transition audit cannot occur after the last-changed time.");
            }

            if (transition.ControllerCommand is { } controllerCommand)
            {
                if (controllerCommand.Trigger != transition.Trigger
                    || controllerCommand.IssuedAtUtc != transition.OccurredAtUtc)
                {
                    throw new InvalidOperationException(
                        "Station controller command audit does not match its transition.");
                }
            }

            if (transition.FromState != expectedFromState)
            {
                throw new InvalidOperationException(
                    "Station transition audit does not form a continuous state history.");
            }

            var decision = Policy.Evaluate(
                transition.FromState,
                transition.Trigger,
                transition.Readiness);
            if (!decision.Succeeded || decision.ToState != transition.ToState)
            {
                throw new InvalidOperationException(
                    "Station transition audit contains an invalid state transition.");
            }

            expectedFromState = transition.ToState;
            previousAtUtc = transition.OccurredAtUtc;
        }

        if (expectedFromState != State)
        {
            throw new InvalidOperationException(
                "Station state does not match its transition audit.");
        }

        if (PendingControllerCommand is { } pending
            && (_transitionAudit.Count == 0
                || _transitionAudit[^1].ControllerCommand != pending
                || _transitionAudit[^1].ToState != State))
        {
            throw new InvalidOperationException(
                "Pending Station controller command does not match the latest transition.");
        }

        if (PendingControllerRecovery is { } recovery
            && recovery.RequiredAtUtc > LastChangedAtUtc)
        {
            throw new InvalidOperationException(
                "Pending Station controller recovery cannot occur after last-changed time.");
        }

        if (PendingControllerCommandDelivery is { } delivery
            && (PendingControllerCommand is not { } pendingCommand
                || !string.Equals(
                    delivery.CommandId,
                    pendingCommand.CommandId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    delivery.OwnerAgentId,
                    pendingCommand.OwnerAgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    delivery.OwnerAgentInstanceId,
                    pendingCommand.OwnerAgentInstanceId,
                    StringComparison.Ordinal)
                || delivery.FencingToken != pendingCommand.FencingToken
                || delivery.LastClaimedAtUtc > LastChangedAtUtc))
        {
            throw new InvalidOperationException(
                "Pending Station controller command delivery does not match "
                + "the active command lease fence.");
        }

        if (!Readiness.SafetyPermitGranted
            && Policy.RequiresAbortOnSafetyPermitLoss(State))
        {
            throw new InvalidOperationException(
                "Station state is inconsistent with a denied safety permit.");
        }
    }

    private static void ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{typeof(TEnum).Name} value is invalid.");
        }
    }
}

internal static class StationLifecycleGuard
{
    private const int MaximumCanonicalTextLength = 512;

    public static string Canonical(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumCanonicalTextLength
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Any(char.IsControl)
            ? throw new ArgumentException(
                $"{parameterName} must be canonical text no longer than "
                + $"{MaximumCanonicalTextLength} characters and contain no "
                + "control characters.",
                parameterName)
            : value;
    }

    public static void Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName);
        }
    }

    public static void Monotonic(
        DateTimeOffset value,
        DateTimeOffset previous,
        string parameterName)
    {
        Utc(value, parameterName);
        if (value < previous)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Station lifecycle timestamps must be monotonic.");
        }
    }
}
