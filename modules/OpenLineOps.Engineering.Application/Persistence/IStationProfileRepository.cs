using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Stations;

namespace OpenLineOps.Engineering.Application.Persistence;

public interface IStationProfileRepository
{
    Task SaveAsync(StationProfile stationProfile, CancellationToken cancellationToken = default);

    Task<StationProfile?> GetByIdAsync(
        StationProfileId stationProfileId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<StationProfile>> ListAsync(
        CancellationToken cancellationToken = default);
}
