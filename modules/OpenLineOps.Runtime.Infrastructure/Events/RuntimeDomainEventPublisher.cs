using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;

namespace OpenLineOps.Runtime.Infrastructure.Events;

public sealed class RuntimeDomainEventPublisher : IRuntimeDomainEventPublisher
{
    private readonly IReadOnlyCollection<IRuntimeDomainEventSubscriber> _subscribers;

    public RuntimeDomainEventPublisher(IEnumerable<IRuntimeDomainEventSubscriber> subscribers)
    {
        ArgumentNullException.ThrowIfNull(subscribers);
        _subscribers = subscribers.ToArray();
    }

    public async ValueTask PublishAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var subscriber in _subscribers)
        {
            await subscriber.HandleAsync(domainEvents, cancellationToken).ConfigureAwait(false);
        }
    }
}
