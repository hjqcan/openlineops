using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryStationLifecycleRepository :
    IStationLifecycleRepository,
    IStationLifecycleFactReader
{
    private readonly object _gate = new();
    private readonly Dictionary<StationId, StoredStationLifecycle> _stations = [];

    public ValueTask<bool> TryAddAsync(
        StationLifecycle station,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stations.ContainsKey(station.Id))
            {
                return ValueTask.FromResult(false);
            }

            var snapshot = station.ToSnapshot();
            var document = StationLifecyclePersistenceJson.Serialize(snapshot);
            var fact = StationLifecycleFactIntegrity.Create(
                station.Id,
                sequence: 1,
                lifecycleRevision: 0,
                document,
                station,
                StationLifecycleFactIntegrity.GenesisSha256);
            _stations.Add(
                station.Id,
                new StoredStationLifecycle(snapshot, Revision: 0, [fact]));
            station.ClearDomainEvents();
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<long> SaveAsync(
        StationLifecycle station,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_stations.TryGetValue(station.Id, out var stored)
                || stored.Revision != expectedRevision)
            {
                throw new StationLifecycleConcurrencyException(
                    station.Id,
                    expectedRevision);
            }

            var nextRevision = checked(expectedRevision + 1);
            var snapshot = station.ToSnapshot();
            var document = StationLifecyclePersistenceJson.Serialize(snapshot);
            var fact = StationLifecycleFactIntegrity.Create(
                station.Id,
                checked(nextRevision + 1),
                nextRevision,
                document,
                station,
                stored.Facts[^1].FactSha256);
            _stations[station.Id] = new StoredStationLifecycle(
                snapshot,
                nextRevision,
                stored.Facts.Append(fact).ToArray());
            station.ClearDomainEvents();
            return ValueTask.FromResult(nextRevision);
        }
    }

    public ValueTask<StationLifecyclePersistenceEntry?> GetByIdAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _stations.TryGetValue(stationId, out var stored)
                    ? new StationLifecyclePersistenceEntry(
                        StationLifecycle.Restore(stored.Snapshot),
                        stored.Revision)
                    : null);
        }
    }

    public ValueTask<IReadOnlyList<StationLifecyclePersistenceEntry>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StationLifecyclePersistenceEntry> entries = _stations
                .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal)
                .Select(static pair => new StationLifecyclePersistenceEntry(
                    StationLifecycle.Restore(pair.Value.Snapshot),
                    pair.Value.Revision))
                .ToArray();
            return ValueTask.FromResult(entries);
        }
    }

    public ValueTask<IReadOnlyList<StationLifecycleFactMetadata>> ListFactsAsync(
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
            IReadOnlyList<StationLifecycleFactMetadata> facts =
                _stations.TryGetValue(stationId, out var stored)
                    ? stored.Facts
                        .Where(fact => fact.Sequence > afterSequence)
                        .Take(pageSize)
                        .ToArray()
                    : [];
            return ValueTask.FromResult(facts);
        }
    }

    private sealed record StoredStationLifecycle(
        StationLifecycleSnapshot Snapshot,
        long Revision,
        IReadOnlyList<StationLifecycleFactMetadata> Facts);
}
