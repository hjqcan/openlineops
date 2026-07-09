namespace OpenLineOps.Domain.Abstractions.EventBus;

public interface ITransactionalIntegrationEventPublisher : IIntegrationEventPublisher
{
    Task PublishTransactionalAsync(
        IEnumerable<object> domainEvents,
        CancellationToken cancellationToken = default);
}
