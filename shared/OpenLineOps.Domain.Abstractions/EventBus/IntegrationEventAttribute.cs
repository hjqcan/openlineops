namespace OpenLineOps.Domain.Abstractions.EventBus;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventAttribute : Attribute
{
    public IntegrationEventAttribute(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name is required.", nameof(eventName));
        }

        EventName = eventName.Trim();
    }

    public string EventName { get; }
}
