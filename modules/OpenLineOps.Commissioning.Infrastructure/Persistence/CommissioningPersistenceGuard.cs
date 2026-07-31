using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Infrastructure.Persistence;

internal static class CommissioningPersistenceGuard
{
    public static void ValidateAppend(
        CommissioningSessionSnapshot stored,
        CommissioningSessionSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(stored.SessionId, candidate.SessionId, StringComparison.Ordinal)
            || !string.Equals(stored.StationId, candidate.StationId, StringComparison.Ordinal)
            || !string.Equals(stored.RequestedBy, candidate.RequestedBy, StringComparison.Ordinal)
            || !string.Equals(stored.AuthorizedRole, candidate.AuthorizedRole, StringComparison.Ordinal)
            || !string.Equals(stored.LeaseId, candidate.LeaseId, StringComparison.Ordinal)
            || stored.StartedAtUtc != candidate.StartedAtUtc
            || !stored.Capabilities.OrderBy(static value => value).SequenceEqual(
                candidate.Capabilities.OrderBy(static value => value)))
        {
            throw new InvalidDataException(
                "Immutable commissioning session identity, lease, actor, role, or capabilities changed.");
        }

        if (candidate.AuditTrail.Count <= stored.AuditTrail.Count)
        {
            throw new InvalidDataException(
                "A commissioning session update must append at least one audit fact.");
        }

        if (!stored.AuditTrail.SequenceEqual(
                candidate.AuditTrail.Take(stored.AuditTrail.Count)))
        {
            throw new InvalidDataException(
                "Persisted commissioning audit history is append-only and cannot be rewritten.");
        }
    }

    public static bool HoldsLease(CommissioningSessionStatus status) =>
        status is CommissioningSessionStatus.Active
            or CommissioningSessionStatus.RecoveryRequired;
}
