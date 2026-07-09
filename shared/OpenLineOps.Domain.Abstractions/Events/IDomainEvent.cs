namespace OpenLineOps.Domain.Abstractions.Events;

public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAtUtc { get; }

    string EventName { get; }
}
