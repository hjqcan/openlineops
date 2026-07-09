using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Events;

public sealed record TraceRecordCreatedDomainEvent(
    TraceRecordId TraceRecordId,
    RuntimeSessionId RuntimeSessionId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();

    public string EventName => "Traceability.TraceRecordCreated";
}
