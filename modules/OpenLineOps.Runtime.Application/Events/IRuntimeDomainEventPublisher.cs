using OpenLineOps.Domain.Abstractions.Events;

namespace OpenLineOps.Runtime.Application.Events;

public interface IRuntimeDomainEventPublisher
{
    ValueTask PublishAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default);
}
