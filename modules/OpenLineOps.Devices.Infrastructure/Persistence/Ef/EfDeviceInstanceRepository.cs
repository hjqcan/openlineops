using Microsoft.EntityFrameworkCore;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;
using OpenLineOps.Infrastructure.Data.Core.Repositories;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

public sealed class EfDeviceInstanceRepository(DevicesDbContext context)
    : BaseRepository<DevicesDbContext, DeviceInstance, DeviceInstanceId>(context),
        IDeviceInstanceRepository
{
    public async ValueTask SaveAsync(
        DeviceInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var exists = await DbSet
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == instance.Id, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            DbSet.Update(instance);
        }
        else
        {
            DbSet.Add(instance);
        }

        await Db.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<DeviceInstance?> IDeviceInstanceRepository.GetByIdAsync(
        DeviceInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        return await GetByIdAsync(instanceId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<DeviceInstance>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var instances = await DbSet
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return OrderById(instances);
    }

    public async ValueTask<IReadOnlyCollection<DeviceInstance>> ListByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return [];
        }

        var normalizedStationId = stationId.Trim();
        var instances = await DbSet
            .AsNoTracking()
            .Where(instance => instance.StationId == normalizedStationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return OrderById(instances);
    }

    private static DeviceInstance[] OrderById(IEnumerable<DeviceInstance> instances)
    {
        return instances
            .OrderBy(instance => instance.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }
}
