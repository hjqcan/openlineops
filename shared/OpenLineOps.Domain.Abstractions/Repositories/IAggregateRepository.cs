using OpenLineOps.Domain.Abstractions.Entities;

namespace OpenLineOps.Domain.Abstractions.Repositories;

public interface IAggregateRepository<TAggregate, TId> : IRepository<TAggregate>
    where TAggregate : Entity<TId>, NetDevPack.Domain.IAggregateRoot
    where TId : notnull
{
    Task<TAggregate?> GetByIdAsync(TId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<TAggregate>> GetAllAsync(CancellationToken cancellationToken = default);

    IQueryable<TAggregate> GetQueryable();

    void Add(TAggregate aggregate);

    void Update(TAggregate aggregate);

    void Remove(TAggregate aggregate);

    Task RemoveByIdAsync(TId id, CancellationToken cancellationToken = default);
}
