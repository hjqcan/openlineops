namespace OpenLineOps.Domain.Abstractions.EventBus;

public sealed record IntegrationEventDescriptor(
    string EventName,
    string Version,
    object Payload)
{
    public IReadOnlyDictionary<string, string?> BuildHeaders(string? correlationId = null)
    {
        return new Dictionary<string, string?>
        {
            ["event-version"] = Version,
            ["event-timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["correlation-id"] = correlationId ?? Guid.NewGuid().ToString(),
            ["aggregate-id"] = ResolveAggregateId(Payload),
            ["event-type"] = Payload.GetType().FullName
        };
    }

    private static string? ResolveAggregateId(object payload)
    {
        var aggregateIdProperty = payload.GetType().GetProperty("AggregateId");
        var aggregateId = aggregateIdProperty?.GetValue(payload);

        return aggregateId?.ToString();
    }
}
