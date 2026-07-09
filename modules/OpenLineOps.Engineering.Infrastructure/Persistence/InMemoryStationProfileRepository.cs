using System.Collections.Concurrent;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Stations;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class InMemoryStationProfileRepository : IStationProfileRepository
{
    private readonly ConcurrentDictionary<string, StationProfile> _stationProfiles = new(StringComparer.Ordinal);

    public Task SaveAsync(StationProfile stationProfile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationProfile);
        cancellationToken.ThrowIfCancellationRequested();

        _stationProfiles[stationProfile.Id.Value] = stationProfile;

        return Task.CompletedTask;
    }

    public Task<StationProfile?> GetByIdAsync(
        StationProfileId stationProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationProfileId);
        cancellationToken.ThrowIfCancellationRequested();

        _stationProfiles.TryGetValue(stationProfileId.Value, out var stationProfile);

        return Task.FromResult(stationProfile);
    }

    public Task<IReadOnlyCollection<StationProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyCollection<StationProfile>>(_stationProfiles.Values.ToArray());
    }
}
