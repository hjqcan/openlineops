using OpenLineOps.Commissioning.Application.Persistence;
using OpenLineOps.Commissioning.Domain.Identifiers;
using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Infrastructure.Persistence;

public sealed class InMemoryCommissioningSessionRepository :
    ICommissioningSessionRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<CommissioningSessionId, StoredSession> _sessions = [];

    public ValueTask<CommissioningAddResult> TryAddAsync(
        CommissioningSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_sessions.ContainsKey(session.Id))
            {
                return ValueTask.FromResult(
                    CommissioningAddResult.SessionAlreadyExists);
            }

            if (_sessions.Values.Any(stored =>
                    string.Equals(
                        stored.Snapshot.StationId,
                        session.StationId,
                        StringComparison.Ordinal)
                    && CommissioningPersistenceGuard.HoldsLease(stored.Snapshot.Status)))
            {
                return ValueTask.FromResult(
                    CommissioningAddResult.StationLeaseConflict);
            }

            _sessions.Add(
                session.Id,
                new StoredSession(session.ToSnapshot(), Revision: 0));
            session.ClearDomainEvents();
            return ValueTask.FromResult(CommissioningAddResult.Added);
        }
    }

    public ValueTask<CommissioningSessionPersistenceEntry?> GetByIdAsync(
        CommissioningSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _sessions.TryGetValue(sessionId, out var stored)
                    ? ToEntry(stored)
                    : null);
        }
    }

    public ValueTask<CommissioningSessionPersistenceEntry?> GetLeaseHolderAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            throw new ArgumentException("Station ID is required.", nameof(stationId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var holder = _sessions.Values.SingleOrDefault(stored =>
                string.Equals(
                    stored.Snapshot.StationId,
                    stationId,
                    StringComparison.Ordinal)
                && CommissioningPersistenceGuard.HoldsLease(stored.Snapshot.Status));
            return ValueTask.FromResult(
                holder is null ? null : ToEntry(holder));
        }
    }

    public ValueTask<long> SaveAsync(
        CommissioningSession session,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.Id, out var stored)
                || stored.Revision != expectedRevision)
            {
                throw new CommissioningSessionConcurrencyException(
                    session.Id,
                    expectedRevision);
            }

            var snapshot = session.ToSnapshot();
            CommissioningPersistenceGuard.ValidateAppend(stored.Snapshot, snapshot);
            if (CommissioningPersistenceGuard.HoldsLease(snapshot.Status)
                && _sessions.Values.Any(other =>
                    !ReferenceEquals(other, stored)
                    && string.Equals(
                        other.Snapshot.StationId,
                        snapshot.StationId,
                        StringComparison.Ordinal)
                    && CommissioningPersistenceGuard.HoldsLease(other.Snapshot.Status)))
            {
                throw new InvalidDataException(
                    $"Station {snapshot.StationId} has more than one commissioning lease holder.");
            }

            var revision = checked(expectedRevision + 1);
            _sessions[session.Id] = new StoredSession(snapshot, revision);
            session.ClearDomainEvents();
            return ValueTask.FromResult(revision);
        }
    }

    private static CommissioningSessionPersistenceEntry ToEntry(StoredSession stored) =>
        new(CommissioningSession.Restore(stored.Snapshot), stored.Revision);

    private sealed record StoredSession(
        CommissioningSessionSnapshot Snapshot,
        long Revision);
}
