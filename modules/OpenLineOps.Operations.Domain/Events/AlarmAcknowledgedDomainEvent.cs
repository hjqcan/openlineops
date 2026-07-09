using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Operations.Domain.Identifiers;

namespace OpenLineOps.Operations.Domain.Events;

public sealed record AlarmAcknowledgedDomainEvent(
    AlarmId AggregateId,
    string AcknowledgedBy,
    DateTimeOffset AcknowledgedAtUtc)
    : DomainEvent("Operations.Alarm.Acknowledged");
