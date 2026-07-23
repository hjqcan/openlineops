using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenLineOps.Runtime.Application.Events;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class ProductionRunCreatedOutboxHostedService(
    IProductionRunCreatedOutboxDispatcher dispatcher,
    ILogger<ProductionRunCreatedOutboxHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly Action<ILogger, Exception?> LogDeliveryFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "ProductionRunCreatedOutboxDeliveryFailed"),
        "Production Run Created-event outbox delivery failed; the durable item remains pending for retry.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetryInterval);
        do
        {
            try
            {
                await dispatcher.DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogDeliveryFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
