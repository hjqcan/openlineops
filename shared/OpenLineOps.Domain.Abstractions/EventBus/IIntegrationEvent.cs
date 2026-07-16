namespace OpenLineOps.Domain.Abstractions.EventBus;

public interface IIntegrationEvent
{
    string EventName { get; }
}
