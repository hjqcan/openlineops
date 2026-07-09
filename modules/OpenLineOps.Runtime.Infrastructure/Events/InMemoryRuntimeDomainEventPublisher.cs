using System.Collections.Concurrent;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Events;

namespace OpenLineOps.Runtime.Infrastructure.Events;

public sealed class InMemoryRuntimeDomainEventPublisher : IRuntimeDomainEventPublisher
{
    private readonly ConcurrentQueue<IDomainEvent> _events = new();
    private readonly IReadOnlyCollection<IRuntimeDomainEventSubscriber> _subscribers;

    public InMemoryRuntimeDomainEventPublisher()
        : this([])
    {
    }

    public InMemoryRuntimeDomainEventPublisher(IEnumerable<IRuntimeDomainEventSubscriber> subscribers)
    {
        ArgumentNullException.ThrowIfNull(subscribers);
        _subscribers = subscribers.ToArray();
    }

    public IReadOnlyCollection<IDomainEvent> Events => _events.ToArray();

    public async ValueTask PublishAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var domainEvent in domainEvents)
        {
            _events.Enqueue(domainEvent);
        }

        foreach (var subscriber in _subscribers)
        {
            await subscriber.HandleAsync(domainEvents, cancellationToken).ConfigureAwait(false);
        }
    }
}
