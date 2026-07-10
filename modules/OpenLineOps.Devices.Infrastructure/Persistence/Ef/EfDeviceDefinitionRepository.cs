using Microsoft.EntityFrameworkCore;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Infrastructure.Data.Core.Repositories;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

public sealed class EfDeviceDefinitionRepository(DevicesDbContext context)
    : BaseRepository<DevicesDbContext, DeviceDefinition, DeviceDefinitionId>(context),
        IDeviceDefinitionRepository
{
    public async ValueTask SaveAsync(
        DeviceDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var exists = await DbSet
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == definition.Id, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            DbSet.Update(definition);
        }
        else
        {
            DbSet.Add(definition);
        }

        await Db.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<DeviceDefinition?> IDeviceDefinitionRepository.GetByIdAsync(
        DeviceDefinitionId definitionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitionId);

        return await GetByIdAsync(definitionId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<DeviceDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await DbSet
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return definitions
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }
}
