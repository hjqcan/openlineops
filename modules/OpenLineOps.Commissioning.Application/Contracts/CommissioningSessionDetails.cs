using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Application.Contracts;

public sealed record CommissioningSessionDetails(
    string SessionId,
    long Revision,
    string StationId,
    string RequestedBy,
    string AuthorizedRole,
    string LeaseId,
    long FencingToken,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    CommissioningSessionStatus Status,
    string? RecoveryReason,
    IReadOnlyCollection<CommissioningCapability> Capabilities,
    IReadOnlyCollection<CommissioningAuditEntry> AuditTrail);
