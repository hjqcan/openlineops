using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IStationLifecycleRepository
{
    ValueTask<bool> TryAddAsync(
        StationLifecycle station,
        CancellationToken cancellationToken = default);

    ValueTask<long> SaveAsync(
        StationLifecycle station,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<StationLifecyclePersistenceEntry?> GetByIdAsync(
        StationId stationId,
        CancellationToken cancellationToken = default);
}

public sealed record StationLifecyclePersistenceEntry
{
    public StationLifecyclePersistenceEntry(StationLifecycle station, long revision)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        Station = station;
        Revision = revision;
    }

    public StationLifecycle Station { get; }

    public long Revision { get; }
}

public sealed class StationLifecycleConcurrencyException : InvalidOperationException
{
    public StationLifecycleConcurrencyException(StationId stationId, long expectedRevision)
        : base(
            $"Station lifecycle {stationId} was not stored at expected revision "
            + $"{expectedRevision}; the caller must reload before applying another transition.")
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        StationId = stationId;
        ExpectedRevision = expectedRevision;
    }

    public StationId StationId { get; }

    public long ExpectedRevision { get; }
}
