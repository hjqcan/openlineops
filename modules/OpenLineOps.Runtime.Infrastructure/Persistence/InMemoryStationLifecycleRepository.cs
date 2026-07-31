using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryStationLifecycleRepository : IStationLifecycleRepository
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

            _stations.Add(
                station.Id,
                new StoredStationLifecycle(station.ToSnapshot(), Revision: 0));
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
            _stations[station.Id] = new StoredStationLifecycle(
                station.ToSnapshot(),
                nextRevision);
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

    private sealed record StoredStationLifecycle(
        StationLifecycleSnapshot Snapshot,
        long Revision);
}
