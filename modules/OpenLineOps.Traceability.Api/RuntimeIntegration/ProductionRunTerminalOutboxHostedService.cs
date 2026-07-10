using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenLineOps.Runtime.Application.Events;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class ProductionRunTerminalOutboxHostedService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly Action<ILogger, Exception?> LogDeliveryFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "ProductionRunTerminalOutboxDeliveryFailed"),
        "Production Run terminal outbox delivery failed; the durable item remains pending for retry.");
    private readonly IProductionRunTerminalOutboxDispatcher _dispatcher;
    private readonly ILogger<ProductionRunTerminalOutboxHostedService> _logger;

    public ProductionRunTerminalOutboxHostedService(
        IProductionRunTerminalOutboxDispatcher dispatcher,
        ILogger<ProductionRunTerminalOutboxHostedService> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetryInterval);
        do
        {
            try
            {
                await _dispatcher.DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogDeliveryFailed(_logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
