using System.Text.Json.Serialization;

namespace OpenLineOps.Commissioning.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StartCommissioningSessionRequest(
    string SessionId,
    string AuthorizedRole,
    string LeaseId,
    long FencingToken,
    int DurationSeconds,
    IReadOnlyCollection<string> Capabilities);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenewCommissioningLeaseRequest(
    long FencingToken,
    int DurationSeconds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommissioningSubjectRequest(
    long FencingToken,
    string SubjectId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuthorizeCommissioningManualCommandRequest(
    long FencingToken,
    string CommandId,
    string StationMode,
    string SafetyClass,
    bool DebugActionWhitelisted);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetCommissioningBreakpointRequest(
    long FencingToken,
    string NodeId,
    string StationMode,
    bool DebugActionWhitelisted);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecoverCommissioningActionRequest(
    long FencingToken,
    string ActionId,
    string IdempotencyClass);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResolveCommissioningRecoveryRequest(
    long FencingToken,
    string Disposition);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompleteCommissioningSessionRequest(long FencingToken);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AbortCommissioningSessionRequest(
    long FencingToken,
    string Reason);

public sealed record CommissioningAuditEntryResponse(
    long Sequence,
    string Kind,
    string ActorId,
    DateTimeOffset OccurredAtUtc,
    string? SubjectId,
    string? Reason,
    long FencingToken);

public sealed record CommissioningSessionResponse(
    string SessionId,
    long Revision,
    string StationId,
    string RequestedBy,
    string AuthorizedRole,
    string LeaseId,
    long FencingToken,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Status,
    string? RecoveryReason,
    IReadOnlyCollection<string> Capabilities,
    IReadOnlyCollection<CommissioningAuditEntryResponse> AuditTrail);
