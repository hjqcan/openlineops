namespace OpenLineOps.Domain.Abstractions.EventBus;

public sealed class IntegrationDtoConverterRegistry
{
    private readonly IReadOnlyCollection<IIntegrationDtoConverter> _converters;

    public IntegrationDtoConverterRegistry(IEnumerable<IIntegrationDtoConverter>? converters = null)
    {
        _converters = converters?.ToArray() ?? [];
    }

    public object ConvertOrOriginal(object domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var converter = _converters.FirstOrDefault(candidate => candidate.CanConvert(domainEvent));

        return converter?.Convert(domainEvent) ?? domainEvent;
    }
}
