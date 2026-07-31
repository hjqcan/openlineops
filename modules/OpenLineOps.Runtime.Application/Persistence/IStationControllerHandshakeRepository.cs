using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IStationControllerHandshakeRepository
{
    ValueTask<StationControllerHandshakePersistenceEntry?> GetByIdAsync(
        StationId stationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StationControllerHandshakeFact>> ListFactsAsync(
        StationId stationId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryAddAsync(
        StationControllerHandshake state,
        StationControllerHandshakeFact initialFact,
        CancellationToken cancellationToken = default);

    ValueTask<long> SaveAsync(
        StationControllerHandshake state,
        long expectedRevision,
        StationControllerHandshakeFact? fact,
        CancellationToken cancellationToken = default);
}

public sealed record StationControllerHandshakeFactRecord(
    StationControllerHandshakeFact Fact,
    string PayloadSha256,
    string PreviousFactSha256,
    string FactSha256);

public interface IStationControllerHandshakeFactReader
{
    ValueTask<IReadOnlyList<StationControllerHandshakeFactRecord>>
        ListFactRecordsAsync(
            StationId stationId,
            long afterSequence = 0,
            int pageSize = 100,
            CancellationToken cancellationToken = default);
}

public sealed record StationControllerHandshakePersistenceEntry
{
    public StationControllerHandshakePersistenceEntry(
        StationControllerHandshake state,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        State = state;
        Revision = revision;
    }

    public StationControllerHandshake State { get; }

    public long Revision { get; }
}

public sealed class StationControllerHandshakeConcurrencyException :
    InvalidOperationException
{
    public StationControllerHandshakeConcurrencyException(
        StationId stationId,
        long expectedRevision)
        : base(
            $"Station controller handshake {stationId} was not stored at expected "
            + $"revision {expectedRevision}; reload before applying another report.")
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        StationId = stationId;
        ExpectedRevision = expectedRevision;
    }

    public StationId StationId { get; }

    public long ExpectedRevision { get; }
}
