using OpenLineOps.Commissioning.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Commissioning.Application.Contracts;

public sealed record StartCommissioningSessionCommand(
    string SessionId,
    string StationId,
    string ActorId,
    string AuthorizedRole,
    string LeaseId,
    long FencingToken,
    TimeSpan Duration,
    IReadOnlyCollection<CommissioningCapability> Capabilities);

public sealed record RenewCommissioningLeaseCommand(
    string SessionId,
    string ActorId,
    long FencingToken,
    TimeSpan Duration);

public sealed record CommissioningSubjectCommand(
    string SessionId,
    string ActorId,
    string SubjectId);

public sealed record AuthorizeCommissioningManualCommand(
    string SessionId,
    string ActorId,
    string CommandId,
    StationMode StationMode,
    CommissioningActionSafetyClass SafetyClass,
    bool DebugActionWhitelisted);

public sealed record SetCommissioningBreakpointCommand(
    string SessionId,
    string ActorId,
    string NodeId,
    StationMode StationMode,
    bool DebugActionWhitelisted);

public sealed record RecoverCommissioningActionCommand(
    string SessionId,
    string ActionId,
    CommissioningActionIdempotencyClass IdempotencyClass);

public sealed record ResolveCommissioningRecoveryCommand(
    string SessionId,
    string ActorId,
    CommissioningRecoveryDisposition Disposition);

public sealed record AbortCommissioningSessionCommand(
    string SessionId,
    string ActorId,
    string Reason);
