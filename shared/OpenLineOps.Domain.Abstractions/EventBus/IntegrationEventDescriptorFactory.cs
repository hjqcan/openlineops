using System.Reflection;

namespace OpenLineOps.Domain.Abstractions.EventBus;

public static class IntegrationEventDescriptorFactory
{
    public static IEnumerable<IntegrationEventDescriptor> Create(IEnumerable<object> domainEvents)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            if (domainEvent is IIntegrationEvent integrationEvent)
            {
                yield return new IntegrationEventDescriptor(
                    integrationEvent.EventName,
                    integrationEvent.Version,
                    domainEvent);
                continue;
            }

            var attribute = domainEvent.GetType().GetCustomAttribute<IntegrationEventAttribute>();
            if (attribute is not null)
            {
                yield return new IntegrationEventDescriptor(
                    attribute.EventName,
                    attribute.Version,
                    domainEvent);
            }
        }
    }

    public static bool IsIntegrationEvent(object domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return domainEvent is IIntegrationEvent
            || domainEvent.GetType().GetCustomAttribute<IntegrationEventAttribute>() is not null;
    }
}
