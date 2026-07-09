namespace OpenLineOps.Domain.Abstractions.Repositories;

public interface IRepository<TAggregate> : NetDevPack.Data.IRepository<TAggregate>
    where TAggregate : NetDevPack.Domain.IAggregateRoot;
