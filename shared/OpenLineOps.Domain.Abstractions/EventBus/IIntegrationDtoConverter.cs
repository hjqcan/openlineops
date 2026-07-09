namespace OpenLineOps.Domain.Abstractions.EventBus;

public interface IIntegrationDtoConverter
{
    bool CanConvert(object domainEvent);

    object Convert(object domainEvent);
}
