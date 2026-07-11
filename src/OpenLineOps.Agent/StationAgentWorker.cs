using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent;

public sealed class StationAgentWorker(
    StationJobCoordinator coordinator,
    IStationJobReceiver receiver,
    IStationSafetyReceiver safetyReceiver,
    IStationSafetyActuator safetyActuator,
    StationJobOutboxDispatcher outboxDispatcher,
    ILogger<StationAgentWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, StationJobId, string, Exception?> LogRecoveryRequired =
        LoggerMessage.Define<StationJobId, string>(
            LogLevel.Critical,
            new EventId(1001, nameof(LogRecoveryRequired)),
            "Station job {JobId} for operation {OperationId} requires physical reconciliation; it was not replayed.");

    private static readonly Action<ILogger, TimeSpan, Exception?> LogDisconnected =
        LoggerMessage.Define<TimeSpan>(
            LogLevel.Error,
            new EventId(1002, nameof(LogDisconnected)),
            "Station job channel disconnected; reconnecting in {RetryDelay}.");

    private static readonly Action<ILogger, StationJobId, ExecutionStatus?, ResultJudgement?, Exception?> LogCompleted =
        LoggerMessage.Define<StationJobId, ExecutionStatus?, ResultJudgement?>(
            LogLevel.Information,
            new EventId(1003, nameof(LogCompleted)),
            "Station job {JobId} completed with execution {ExecutionStatus} and judgement {Judgement}.");

    private static readonly Action<ILogger, TimeSpan, Exception?> LogSafetyDisconnected =
        LoggerMessage.Define<TimeSpan>(
            LogLevel.Critical,
            new EventId(1004, nameof(LogSafetyDisconnected)),
            "Independent station safety channel disconnected; reconnecting in {RetryDelay}.");

    private static readonly Action<ILogger, Guid, bool, string?, Exception?> LogEmergencyStop =
        LoggerMessage.Define<Guid, bool, string?>(
            LogLevel.Critical,
            new EventId(1005, nameof(LogEmergencyStop)),
            "Emergency stop request {RequestId} completed. Accepted={Accepted}, FailureCode={FailureCode}.");

    private static readonly Action<ILogger, Guid, bool, string?, Exception?> LogSafeStop =
        LoggerMessage.Define<Guid, bool, string?>(
            LogLevel.Warning,
            new EventId(1006, nameof(LogSafeStop)),
            "Safe stop request {RequestId} completed. Accepted={Accepted}, FailureCode={FailureCode}.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = await coordinator.RecoverAsync(stoppingToken).ConfigureAwait(false);
        foreach (var job in recovered.Where(job => job.Status == StationJobStatus.RecoveryRequired))
        {
            LogRecoveryRequired(logger, job.JobId, job.OperationId, null);
        }

        await Task.WhenAll(
                ReceiveWithReconnectAsync(stoppingToken),
                ReceiveSafetyWithReconnectAsync(stoppingToken),
                DispatchOutboxAsync(stoppingToken))
            .ConfigureAwait(false);
    }

    private async Task ReceiveSafetyWithReconnectAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromMilliseconds(250);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await safetyReceiver.RunAsync(
                        HandleEmergencyStopAsync,
                        HandleSafeStopAsync,
                        stoppingToken)
                    .ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(250);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogSafetyDisconnected(logger, retryDelay, exception);
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(5, retryDelay.TotalSeconds * 2));
            }
        }

        async ValueTask<StationSafetyExecutionResult> HandleEmergencyStopAsync(
            EmergencyStopRequested request,
            CancellationToken cancellationToken)
        {
            var result = await safetyActuator
                .EmergencyStopAsync(request, cancellationToken)
                .ConfigureAwait(false);
            LogEmergencyStop(
                logger,
                request.MessageId,
                result.Accepted,
                result.FailureCode,
                null);
            return result;
        }

        async ValueTask<StationSafetyExecutionResult> HandleSafeStopAsync(
            StationSafeStopRequested request,
            CancellationToken cancellationToken)
        {
            var result = await safetyActuator
                .SafeStopAsync(request, cancellationToken)
                .ConfigureAwait(false);
            LogSafeStop(
                logger,
                request.MessageId,
                result.Accepted,
                result.FailureCode,
                null);
            return result;
        }
    }

    private async Task ReceiveWithReconnectAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await receiver.RunAsync(HandleJobAsync, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogDisconnected(logger, retryDelay, exception);
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
            }
        }

        async ValueTask HandleJobAsync(
            Contracts.StationJobRequested request,
            CancellationToken cancellationToken)
        {
            var result = await coordinator.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            LogCompleted(
                logger,
                result.JobId,
                result.ExecutionStatus,
                result.Judgement,
                null);
        }
    }

    private async Task DispatchOutboxAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        do
        {
            var dispatched = await outboxDispatcher.DispatchAsync(100, stoppingToken)
                .ConfigureAwait(false);
            if (dispatched == 100)
            {
                continue;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
