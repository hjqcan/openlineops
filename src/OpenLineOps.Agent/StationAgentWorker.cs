using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent;

internal sealed class StationAgentWorker(
    StationJobCoordinator coordinator,
    StationResourceLeaseChangeCoordinator resourceLeaseChanges,
    IStationJobReceiver receiver,
    IStationSafetyReceiver safetyReceiver,
    IStationSafetyActuator safetyActuator,
    StationJobOutboxDispatcher outboxDispatcher,
    StationAgentShutdownState shutdownState,
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

    private static readonly Action<ILogger, Guid, Guid, bool, string?, Exception?> LogJobCancel =
        LoggerMessage.Define<Guid, Guid, bool, string?>(
            LogLevel.Warning,
            new EventId(1007, nameof(LogJobCancel)),
            "Station job cancellation request {RequestId} for job {JobId} completed. Accepted={Accepted}, FailureCode={FailureCode}.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerLoopsStarted = false;
        var quiesced = false;
        try
        {
            var recovered = await coordinator.RecoverAsync(stoppingToken).ConfigureAwait(false);
            foreach (var job in recovered.Where(job =>
                         job.Status == StationJobStatus.RecoveryRequired))
            {
                LogRecoveryRequired(logger, job.JobId, job.OperationId, null);
            }

            workerLoopsStarted = true;
            await RunWorkerLoopsAsync(stoppingToken).ConfigureAwait(false);
            quiesced = true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            quiesced = true;
        }
        finally
        {
            if (!workerLoopsStarted || quiesced)
            {
                shutdownState.MarkWorkerQuiesced();
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StationAgentWorkerLifecycle.RequireQuiescenceAsync(
                base.StopAsync(cancellationToken),
                shutdownState,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunWorkerLoopsAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task[] loops =
        [
            ReceiveWithReconnectAsync(lifetime.Token),
            ReceiveSafetyWithReconnectAsync(lifetime.Token),
            DispatchOutboxAsync(lifetime.Token)
        ];
        await StationAgentWorkerLifecycle.AwaitLoopsAsync(
                loops,
                lifetime,
                stoppingToken)
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
                        HandleJobCancelAsync,
                        stoppingToken)
                    .ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(250);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (ContainsDeliveryDrainTimeout(exception))
            {
                throw;
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                throw;
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

        async ValueTask<StationJobCancelExecutionResult> HandleJobCancelAsync(
            StationJobCancelRequested request,
            CancellationToken cancellationToken)
        {
            var result = await coordinator.CancelAsync(request, cancellationToken)
                .ConfigureAwait(false);
            LogJobCancel(
                logger,
                request.MessageId,
                request.JobId,
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
                await receiver.RunAsync(
                        HandleJobAsync,
                        resourceLeaseChanges.HandleAsync,
                        stoppingToken)
                    .ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (ContainsDeliveryDrainTimeout(exception))
            {
                throw;
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                throw;
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

    private static bool ContainsDeliveryDrainTimeout(Exception exception) =>
        exception is StationDeliveryDrainTimeoutException
        || exception is AggregateException aggregate
        && aggregate.Flatten().InnerExceptions.Any(static inner =>
            inner is StationDeliveryDrainTimeoutException);
}
