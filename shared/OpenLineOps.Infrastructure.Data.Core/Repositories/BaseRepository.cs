using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Domain.Abstractions.Repositories;

namespace OpenLineOps.Infrastructure.Data.Core.Repositories;

public abstract class BaseRepository<TContext, TAggregate, TId> :
    IAggregateRepository<TAggregate, TId>,
    IDisposable
    where TContext : DbContext, NetDevPack.Data.IUnitOfWork
    where TAggregate : Entity<TId>, NetDevPack.Domain.IAggregateRoot
    where TId : notnull
{
    protected BaseRepository(TContext context)
    {
        Db = context ?? throw new ArgumentNullException(nameof(context));
        DbSet = Db.Set<TAggregate>();
    }

    protected TContext Db { get; }

    protected DbSet<TAggregate> DbSet { get; }

    public NetDevPack.Data.IUnitOfWork UnitOfWork => Db;

    public virtual async Task<TAggregate?> GetByIdAsync(
        TId id,
        CancellationToken cancellationToken = default)
    {
        return await DbSet
            .AsNoTracking()
            .FirstOrDefaultAsync(BuildIdPredicate(id), cancellationToken)
            .ConfigureAwait(false);
    }

    public virtual async Task<IReadOnlyCollection<TAggregate>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await DbSet
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public virtual IQueryable<TAggregate> GetQueryable()
    {
        return DbSet.AsNoTracking();
    }

    public virtual void Add(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        DbSet.Add(aggregate);
    }

    public virtual void Update(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        DbSet.Update(aggregate);
    }

    public virtual void Remove(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        DbSet.Remove(aggregate);
    }

    public virtual async Task RemoveByIdAsync(
        TId id,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await DbSet
            .FirstOrDefaultAsync(BuildIdPredicate(id), cancellationToken)
            .ConfigureAwait(false);

        if (aggregate is not null)
        {
            DbSet.Remove(aggregate);
        }
    }

    public virtual void Dispose()
    {
        Db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Expression<Func<TAggregate, bool>> BuildIdPredicate(TId id)
    {
        var aggregate = Expression.Parameter(typeof(TAggregate), "aggregate");
        var idProperty = Expression.Property(aggregate, nameof(Entity<TId>.Id));
        var idValue = Expression.Constant(id, typeof(TId));
        var equal = Expression.Equal(idProperty, idValue);

        return Expression.Lambda<Func<TAggregate, bool>>(equal, aggregate);
    }
}
