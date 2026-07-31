using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryStationControllerHandshakeRepository :
    IStationControllerHandshakeRepository,
    IStationControllerHandshakeFactReader
{
    private readonly object _gate = new();
    private readonly Dictionary<StationId, StoredHandshake> _states = [];

    public ValueTask<StationControllerHandshakePersistenceEntry?> GetByIdAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _states.TryGetValue(stationId, out var stored)
                    ? new StationControllerHandshakePersistenceEntry(
                        StationControllerHandshake.Restore(stored.Snapshot),
                        stored.Revision)
                    : null);
        }
    }

    public ValueTask<IReadOnlyList<StationControllerHandshakeFact>> ListFactsAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StationControllerHandshakeFact> facts =
                _states.TryGetValue(stationId, out var stored)
                    ? stored.Facts.Select(static record => record.Fact).ToArray()
                    : [];
            return ValueTask.FromResult(facts);
        }
    }

    public ValueTask<IReadOnlyList<StationControllerHandshakeFactRecord>>
        ListFactRecordsAsync(
            StationId stationId,
            long afterSequence = 0,
            int pageSize = 100,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StationControllerHandshakeFactRecord> records =
                _states.TryGetValue(stationId, out var stored)
                    ? stored.Facts
                        .Where(record => record.Fact.Sequence > afterSequence)
                        .Take(pageSize)
                        .ToArray()
                    : [];
            return ValueTask.FromResult(records);
        }
    }

    public ValueTask<bool> TryAddAsync(
        StationControllerHandshake state,
        StationControllerHandshakeFact initialFact,
        CancellationToken cancellationToken = default)
    {
        ValidateFact(state, initialFact, expectedSequence: 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_states.ContainsKey(state.StationId))
            {
                return ValueTask.FromResult(false);
            }

            _states.Add(
                state.StationId,
                new StoredHandshake(
                    state.ToSnapshot(),
                    Revision: 0,
                    [CreateRecord(
                        initialFact,
                        StationControllerHandshakeFactIntegrity.GenesisSha256)]));
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<long> SaveAsync(
        StationControllerHandshake state,
        long expectedRevision,
        StationControllerHandshakeFact? fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (fact is not null)
        {
            ValidateFact(state, fact, checked(expectedRevision + 2));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_states.TryGetValue(state.StationId, out var stored)
                || stored.Revision != expectedRevision)
            {
                throw new StationControllerHandshakeConcurrencyException(
                    state.StationId,
                    expectedRevision);
            }

            var revision = checked(expectedRevision + 1);
            var facts = fact is null
                ? stored.Facts
                : stored.Facts.Append(CreateRecord(
                    fact,
                    stored.Facts[^1].FactSha256)).ToArray();
            _states[state.StationId] = new StoredHandshake(
                state.ToSnapshot(),
                revision,
                facts);
            return ValueTask.FromResult(revision);
        }
    }

    private static void ValidateFact(
        StationControllerHandshake state,
        StationControllerHandshakeFact fact,
        long expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.StationId != state.StationId
            || fact.Sequence != expectedSequence)
        {
            throw new ArgumentException(
                "Controller handshake fact identity or sequence does not match "
                + "the state revision.",
                nameof(fact));
        }
    }

    private sealed record StoredHandshake(
        StationControllerHandshakeSnapshot Snapshot,
        long Revision,
        IReadOnlyList<StationControllerHandshakeFactRecord> Facts);

    private static StationControllerHandshakeFactRecord CreateRecord(
        StationControllerHandshakeFact fact,
        string previousFactSha256)
    {
        var document = StationControllerHandshakePersistenceJson.SerializeFact(
            fact);
        return StationControllerHandshakeFactIntegrity.Create(
            fact,
            document,
            previousFactSha256);
    }
}
