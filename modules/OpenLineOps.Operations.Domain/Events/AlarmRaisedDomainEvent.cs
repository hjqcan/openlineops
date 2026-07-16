using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Operations.Domain.Shared.IntegrationEvents;

namespace OpenLineOps.Operations.Domain.Events;

public sealed record AlarmRaisedDomainEvent(
    AlarmId AggregateId,
    string StationId,
    string Source,
    string? SourceId,
    AlarmSeverity Severity,
    string Title,
    string Description,
    DateTimeOffset RaisedAtUtc)
    : DomainEvent(AlarmRaisedIntegrationDto.EventName),
        IIntegrationEvent;
