using System.Text.Json;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent;

internal sealed record StationAgentPresenceOptions(
    string AgentId,
    string StationId,
    string StationSystemId,
    TimeSpan HeartbeatInterval);

internal sealed class StationAgentPresenceWorker(
    StationAgentPresenceOptions options,
    IStationAgentMessagePublisher publisher,
    IClock clock,
    StationAgentShutdownState shutdownState,
    ILogger<StationAgentPresenceWorker> logger) : BackgroundService
{
    private const int StoppingPublishAttemptLimit = 3;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan StoppingPublishRetryDelay =
        TimeSpan.FromMilliseconds(100);
    private static readonly Action<ILogger, AgentPresenceState, long, Exception?> LogPublished =
        LoggerMessage.Define<AgentPresenceState, long>(
            LogLevel.Debug,
            new EventId(1101, nameof(LogPublished)),
            "Station Agent presence {PresenceState} sequence {Sequence} was confirmed.");
    private static readonly Action<ILogger, AgentPresenceState, long, Exception?> LogPublishFailed =
        LoggerMessage.Define<AgentPresenceState, long>(
            LogLevel.Warning,
            new EventId(1102, nameof(LogPublishFailed)),
            "Station Agent presence {PresenceState} sequence {Sequence} was not confirmed; the presence policy will retry when allowed.");
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private Task? _stopTask;
    private AgentPresenceReported? _stoppingMessage;
    private bool _disposeRequested;
    private long _lastIssuedSequence;
    private int _startedConfirmed;
    private int _stopping;
    private int _executeStarted;
    private int _publishGateDisposed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Volatile.Write(ref _executeStarted, 1);
        try
        {
            var started = CreateMessage(AgentPresenceState.Started, sequence: 1);
            while (Volatile.Read(ref _stopping) == 0
                   && !await TryPublishStartedAsync(started, stoppingToken)
                       .ConfigureAwait(false))
            {
                await Task.Delay(options.HeartbeatInterval, stoppingToken)
                    .ConfigureAwait(false);
            }

            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            using var timer = new PeriodicTimer(options.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _stopping) == 0)
                {
                    await PublishNextAsync(AgentPresenceState.Heartbeat, stoppingToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var stopTask = GetOrCreateStopTask(cancellationToken);
            try
            {
                await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested
                      && stopTask.IsCanceled)
            {
            }
        }
    }

    public override void Dispose()
    {
        Task executeTask;
        Task? stopTask;
        lock (_lifecycleSync)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            executeTask = ExecuteTask ?? Task.CompletedTask;
            stopTask = _stopTask;
        }

        base.Dispose();
        var lifecycleCompletion = stopTask is null
            ? executeTask
            : Task.WhenAll(executeTask, stopTask);
        if (lifecycleCompletion.IsCompleted)
        {
            _ = lifecycleCompletion.Exception;
            DisposePublishGate();
            return;
        }

        _ = lifecycleCompletion.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((StationAgentPresenceWorker)state!).DisposePublishGate();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task GetOrCreateStopTask(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            if (_stopTask is null || _stopTask.IsCanceled)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                _stopTask = StopCoreAsync(cancellationToken);
            }

            return _stopTask;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 1);
        shutdownState.BeginShutdown();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await shutdownState.WaitForWorkerQuiescenceAsync(cancellationToken)
            .ConfigureAwait(false);
        if (Volatile.Read(ref _executeStarted) == 1
            && Volatile.Read(ref _startedConfirmed) == 1)
        {
            await PublishStoppingAsync(
                    GetOrCreateStoppingMessage(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private AgentPresenceReported GetOrCreateStoppingMessage()
    {
        lock (_lifecycleSync)
        {
            return _stoppingMessage ??= CreateMessage(
                AgentPresenceState.Stopping,
                ReserveNextSequence());
        }
    }

    private void DisposePublishGate()
    {
        if (Interlocked.Exchange(ref _publishGateDisposed, 1) == 0)
        {
            _publishGate.Dispose();
        }
    }

    private async ValueTask PublishNextAsync(
        AgentPresenceState state,
        CancellationToken cancellationToken)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state == AgentPresenceState.Heartbeat
                && Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            var sequence = ReserveNextSequence();
            await TryPublishCoreAsync(
                    CreateMessage(state, sequence),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async ValueTask<bool> TryPublishStartedAsync(
        AgentPresenceReported message,
        CancellationToken cancellationToken)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var published = await TryPublishCoreAsync(message, cancellationToken)
                .ConfigureAwait(false);
            if (published)
            {
                Volatile.Write(ref _lastIssuedSequence, message.Sequence);
                Volatile.Write(ref _startedConfirmed, 1);
            }

            return published;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async ValueTask<bool> TryPublishCoreAsync(
        AgentPresenceReported message,
        CancellationToken cancellationToken)
    {
        try
        {
            await PublishCoreAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogPublishFailed(
                logger,
                message.State,
                message.Sequence,
                StationAgentDiagnostics.CreateLogSafeException(
                    "Station Agent presence broker publication failed",
                    exception));
            return false;
        }
    }

    private async ValueTask PublishStoppingAsync(
        AgentPresenceReported message,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= StoppingPublishAttemptLimit; attempt++)
        {
            try
            {
                await PublishCoreAsync(message, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                LogPublishFailed(
                    logger,
                    message.State,
                    message.Sequence,
                    StationAgentDiagnostics.CreateLogSafeException(
                        "Station Agent Stopping presence broker publication failed",
                        exception));
                if (attempt < StoppingPublishAttemptLimit)
                {
                    await Task.Delay(StoppingPublishRetryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            $"Station Agent Stopping presence was not broker-confirmed after {StoppingPublishAttemptLimit} attempts.",
            lastFailure);
    }

    private async ValueTask PublishCoreAsync(
        AgentPresenceReported message,
        CancellationToken cancellationToken)
    {
        AgentPresenceContract.Validate(message);
        await publisher.PublishAsync(
                nameof(AgentPresenceReported),
                JsonSerializer.Serialize(message, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        LogPublished(logger, message.State, message.Sequence, null);
    }

    private AgentPresenceReported CreateMessage(
        AgentPresenceState state,
        long sequence) => new(
            options.AgentId,
            options.StationId,
            options.StationSystemId,
            _sessionId,
            sequence,
            state,
            clock.UtcNow);

    private long ReserveNextSequence()
    {
        var sequence = checked(Volatile.Read(ref _lastIssuedSequence) + 1);
        Volatile.Write(ref _lastIssuedSequence, sequence);
        return sequence;
    }
}
