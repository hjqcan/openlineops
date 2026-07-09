using Microsoft.EntityFrameworkCore;
using OpenLineOps.Infrastructure.Data.Core.Repositories;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Repositories;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

public sealed class EfAlarmRepository(OperationsDbContext context)
    : BaseRepository<OperationsDbContext, Alarm, AlarmId>(context),
        IAlarmRepository
{
    public override async Task<Alarm?> GetByIdAsync(
        AlarmId id,
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        return await base.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyCollection<Alarm>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        return await base.GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Add(Alarm aggregate)
    {
        Db.EnsureSchemaReady();

        base.Add(aggregate);
    }

    public override void Update(Alarm aggregate)
    {
        Db.EnsureSchemaReady();

        base.Update(aggregate);
    }

    public override void Remove(Alarm aggregate)
    {
        Db.EnsureSchemaReady();

        base.Remove(aggregate);
    }

    public override async Task RemoveByIdAsync(
        AlarmId id,
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        await base.RemoveByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<Alarm>> GetOpenByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return [];
        }

        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        return await DbSet
            .AsNoTracking()
            .Where(alarm => alarm.StationId == stationId.Trim()
                && alarm.Status != AlarmStatus.Resolved)
            .OrderByDescending(alarm => alarm.RaisedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<Alarm>> GetByStatusAsync(
        AlarmStatus status,
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        return await DbSet
            .AsNoTracking()
            .Where(alarm => alarm.Status == status)
            .OrderByDescending(alarm => alarm.RaisedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
