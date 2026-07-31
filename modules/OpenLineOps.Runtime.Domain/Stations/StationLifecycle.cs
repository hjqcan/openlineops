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
    DateTimeOffset OccurredAtUtc);

public sealed record StationLifecycleSnapshot(
    StationId StationId,
    StationMode Mode,
    StationState State,
    StationReadiness Readiness,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    IReadOnlyList<StationTransitionAuditEntry> TransitionAudit);

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
        DateTimeOffset acknowledgedAtUtc) =>
        Execute(
            StationTransitionTrigger.StateCompleted,
            StationCommandGrant.AcknowledgeTransition,
            authorization,
            reason,
            acknowledgedAtUtc);

    public StationLifecycleSnapshot ToSnapshot()
    {
        return new StationLifecycleSnapshot(
            Id,
            Mode,
            State,
            Readiness,
            CreatedAtUtc,
            LastChangedAtUtc,
            TransitionAudit.ToArray());
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
    public static string Canonical(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
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
