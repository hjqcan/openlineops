using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Operations.Domain.Shared.IntegrationEvents;

namespace OpenLineOps.Operations.Domain.Events.Converters;

public static class AlarmEventToIntegrationDtoConverter
{
    public static AlarmRaisedIntegrationDto ToIntegrationDto(
        this AlarmRaisedDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return new AlarmRaisedIntegrationDto(
            domainEvent.AggregateId.Value,
            domainEvent.StationId,
            domainEvent.Source,
            domainEvent.SourceId,
            domainEvent.Severity,
            domainEvent.Title,
            domainEvent.Description,
            domainEvent.RaisedAtUtc);
    }
}

public sealed class AlarmIntegrationDtoConverter : IIntegrationDtoConverter
{
    public bool CanConvert(object domainEvent)
    {
        return domainEvent is AlarmRaisedDomainEvent;
    }

    public object Convert(object domainEvent)
    {
        return domainEvent switch
        {
            AlarmRaisedDomainEvent alarmRaised => alarmRaised.ToIntegrationDto(),
            _ => throw new NotSupportedException(
                $"Unsupported integration event type: {domainEvent.GetType().Name}.")
        };
    }
}
