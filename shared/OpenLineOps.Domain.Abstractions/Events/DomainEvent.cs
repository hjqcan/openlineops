namespace OpenLineOps.Domain.Abstractions.Events;

public abstract record DomainEvent(string EventName) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
