using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Operations.Domain.Identifiers;

namespace OpenLineOps.Operations.Domain.Events;

public sealed record AlarmResolvedDomainEvent(
    AlarmId AggregateId,
    string ResolvedBy,
    DateTimeOffset ResolvedAtUtc,
    string ResolutionNote)
    : DomainEvent("Operations.Alarm.Resolved");
