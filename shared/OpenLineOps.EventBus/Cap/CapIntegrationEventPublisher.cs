using System.Diagnostics;
using DotNetCore.CAP;
using Microsoft.Extensions.Logging;
using OpenLineOps.Domain.Abstractions.EventBus;

namespace OpenLineOps.EventBus.Cap;

public sealed class CapIntegrationEventPublisher : ITransactionalIntegrationEventPublisher
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogIntegrationEventPublished =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogIntegrationEventPublished)),
            "Published integration event {EventName} {Version} with payload {PayloadType}.");

    private static readonly Action<ILogger, string, string, Exception?> LogIntegrationEventPublishFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2, nameof(LogIntegrationEventPublishFailed)),
            "Integration event publish failed for {EventName} {Version}.");

    private readonly ICapPublisher _capPublisher;
    private readonly IntegrationDtoConverterRegistry _dtoConverterRegistry;
    private readonly ILogger<CapIntegrationEventPublisher> _logger;

    public CapIntegrationEventPublisher(
        ICapPublisher capPublisher,
        IntegrationDtoConverterRegistry dtoConverterRegistry,
        ILogger<CapIntegrationEventPublisher> logger)
    {
        _capPublisher = capPublisher;
        _dtoConverterRegistry = dtoConverterRegistry;
        _logger = logger;
    }

    public async Task PublishAsync(
        IEnumerable<object> domainEvents,
        CancellationToken cancellationToken = default)
    {
        await PublishCoreAsync(
            domainEvents,
            throwOnFailure: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishTransactionalAsync(
        IEnumerable<object> domainEvents,
        CancellationToken cancellationToken = default)
    {
        await PublishCoreAsync(
            domainEvents,
            throwOnFailure: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishCoreAsync(
        IEnumerable<object> domainEvents,
        bool throwOnFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        var descriptors = IntegrationEventDescriptorFactory.Create(domainEvents).ToArray();
        if (descriptors.Length == 0)
        {
            return;
        }

        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        foreach (var descriptor in descriptors)
        {
            try
            {
                var payload = _dtoConverterRegistry.ConvertOrOriginal(descriptor.Payload);
                await _capPublisher
                    .PublishAsync(
                        descriptor.EventName,
                        payload,
                        BuildCapHeaders(descriptor, correlationId),
                        cancellationToken)
                    .ConfigureAwait(false);

                LogIntegrationEventPublished(
                    _logger,
                    descriptor.EventName,
                    descriptor.Version,
                    payload.GetType().FullName ?? payload.GetType().Name,
                    null);
            }
            catch (Exception ex)
            {
                LogIntegrationEventPublishFailed(
                    _logger,
                    descriptor.EventName,
                    descriptor.Version,
                    ex);

                if (throwOnFailure)
                {
                    throw;
                }
            }
        }
    }

    private static Dictionary<string, string?> BuildCapHeaders(
        IntegrationEventDescriptor descriptor,
        string correlationId)
    {
        return descriptor.BuildHeaders(correlationId)
            .Where(header => !string.IsNullOrWhiteSpace(header.Value))
            .ToDictionary(
                header => header.Key,
                header => (string?)header.Value,
                StringComparer.Ordinal);
    }
}
