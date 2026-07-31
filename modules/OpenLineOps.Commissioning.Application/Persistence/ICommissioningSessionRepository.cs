using OpenLineOps.Commissioning.Domain.Identifiers;
using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Application.Persistence;

public interface ICommissioningSessionRepository
{
    ValueTask<CommissioningAddResult> TryAddAsync(
        CommissioningSession session,
        CancellationToken cancellationToken = default);

    ValueTask<CommissioningSessionPersistenceEntry?> GetByIdAsync(
        CommissioningSessionId sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<CommissioningSessionPersistenceEntry?> GetLeaseHolderAsync(
        string stationId,
        CancellationToken cancellationToken = default);

    ValueTask<long> SaveAsync(
        CommissioningSession session,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed record CommissioningSessionPersistenceEntry(
    CommissioningSession Session,
    long Revision);

public enum CommissioningAddResult
{
    Added = 0,
    SessionAlreadyExists = 1,
    StationLeaseConflict = 2
}

public sealed class CommissioningSessionConcurrencyException : InvalidOperationException
{
    public CommissioningSessionConcurrencyException(
        CommissioningSessionId sessionId,
        long expectedRevision)
        : base(
            $"Commissioning session {sessionId} was not stored at expected revision "
            + $"{expectedRevision}; reload the session before applying another operation.")
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
    }

    public CommissioningSessionId SessionId { get; }

    public long ExpectedRevision { get; }
}
