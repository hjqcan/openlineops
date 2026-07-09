using OpenLineOps.Domain.Abstractions.Events;

namespace OpenLineOps.Runtime.Application.Events;

public interface IRuntimeDomainEventSubscriber
{
    ValueTask HandleAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default);
}
