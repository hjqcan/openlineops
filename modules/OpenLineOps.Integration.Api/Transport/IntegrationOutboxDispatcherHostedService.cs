using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenLineOps.Integration.Application.Outbox;

namespace OpenLineOps.Integration.Api.Transport;

public sealed class IntegrationOutboxDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<IntegrationOutboxWorkerOptions> options,
    ILogger<IntegrationOutboxDispatcherHostedService> logger)
    : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogDispatchFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4101, "IntegrationOutboxDispatchFailure"),
            "Integration outbox dispatch iteration failed.");

    private readonly IntegrationOutboxWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = _options.PollInterval;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var connector = scope.ServiceProvider
                    .GetRequiredService<IIntegrationConnector>();
                if (connector is IIntegrationConnectorReadiness
                    {
                        IsReady: false
                    })
                {
                    delay = _options.ConnectorUnavailableInterval;
                }
                else
                {
                    var dispatcher = scope.ServiceProvider
                        .GetRequiredService<IntegrationOutboxDispatcher>();
                    var delivered = await dispatcher.DispatchAsync(
                            _options.BatchSize,
                            timeProvider.GetUtcNow(),
                            stoppingToken)
                        .ConfigureAwait(false);
                    delay = delivered == _options.BatchSize
                        ? TimeSpan.Zero
                        : _options.PollInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogDispatchFailure(logger, exception);
                delay = _options.FailureInterval;
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
