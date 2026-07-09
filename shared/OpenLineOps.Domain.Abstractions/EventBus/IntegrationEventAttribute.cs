namespace OpenLineOps.Domain.Abstractions.EventBus;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventAttribute : Attribute
{
    public IntegrationEventAttribute(string eventName, string version = "v1")
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name is required.", nameof(eventName));
        }

        EventName = eventName.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? "v1" : version.Trim();
    }

    public string EventName { get; }

    public string Version { get; }
}
