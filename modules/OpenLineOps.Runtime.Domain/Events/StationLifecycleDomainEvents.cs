using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record StationLifecycleCreatedDomainEvent(
    StationId StationId,
    StationMode Mode,
    StationState State,
    StationReadiness Readiness,
    string ActorId)
    : DomainEvent("StationLifecycle.Created");

public sealed record StationModeChangedDomainEvent(
    StationId StationId,
    StationMode FromMode,
    StationMode ToMode,
    string ActorId,
    string Reason)
    : DomainEvent("StationLifecycle.ModeChanged");

public sealed record StationReadinessChangedDomainEvent(
    StationId StationId,
    StationReadiness FromReadiness,
    StationReadiness ToReadiness,
    string ActorId,
    string Reason)
    : DomainEvent("StationLifecycle.ReadinessChanged");

public sealed record StationStateTransitionedDomainEvent(
    StationId StationId,
    StationTransitionAuditEntry Transition)
    : DomainEvent("StationLifecycle.StateTransitioned");

public sealed record StationControllerCommandIssuedDomainEvent(
    StationId StationId,
    StationControllerCommandExpectation Command)
    : DomainEvent("StationLifecycle.ControllerCommandIssued");

public sealed record StationControllerCommandDeliveryClaimedDomainEvent(
    StationId StationId,
    StationControllerCommandDeliveryClaim Claim)
    : DomainEvent("StationLifecycle.ControllerCommandDeliveryClaimed");

public sealed record StationControllerRecoveryRequiredDomainEvent(
    StationId StationId,
    StationControllerRecoveryIntent Intent)
    : DomainEvent("StationLifecycle.ControllerRecoveryRequired");

public sealed record StationControllerRecoverySynchronizedDomainEvent(
    StationId StationId,
    string IntentId)
    : DomainEvent("StationLifecycle.ControllerRecoverySynchronized");
