using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenLineOps.Runtime.Application.Stations;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class StationControllerCommandWatchdog(
    IServiceScopeFactory scopeFactory,
    ILogger<StationControllerCommandWatchdog> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly Action<ILogger, int, Exception?> LogExpiredCommands =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(4101, "StationControllerCommandsExpired"),
            "Expired {ExpiredControllerCommandCount} Station controller "
            + "command(s); affected lifecycles were aborted and marked for recovery.");
    private static readonly Action<ILogger, Exception?> LogIterationFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4102, "StationControllerCommandWatchdogFailed"),
            "Station controller command watchdog iteration failed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider
                    .GetRequiredService<StationLifecycleService>();
                var expired = await service.ExpireControllerCommandsAsync(
                        stoppingToken)
                    .ConfigureAwait(false);
                if (expired > 0)
                {
                    LogExpiredCommands(logger, expired, null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogIterationFailure(logger, exception);
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
