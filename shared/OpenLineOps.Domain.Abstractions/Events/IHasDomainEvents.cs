namespace OpenLineOps.Domain.Abstractions.Events;

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void RemoveDomainEvent(IDomainEvent domainEvent);

    void ClearDomainEvents();
}
