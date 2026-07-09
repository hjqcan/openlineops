namespace OpenLineOps.Domain.Abstractions.EventBus;

public interface IIntegrationEventPublisher
{
    Task PublishAsync(
        IEnumerable<object> domainEvents,
        CancellationToken cancellationToken = default);
}
