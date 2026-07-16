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
    ILogger<StationAgentPresenceWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, AgentPresenceState, long, Exception?> LogPublished =
        LoggerMessage.Define<AgentPresenceState, long>(
            LogLevel.Debug,
            new EventId(1101, nameof(LogPublished)),
            "Station Agent presence {PresenceState} sequence {Sequence} was confirmed.");
    private static readonly Action<ILogger, AgentPresenceState, long, Exception?> LogPublishFailed =
        LoggerMessage.Define<AgentPresenceState, long>(
            LogLevel.Warning,
            new EventId(1102, nameof(LogPublishFailed)),
            "Station Agent presence {PresenceState} sequence {Sequence} was not confirmed; a later heartbeat will retry liveness.");
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private long _sequence;
    private int _stopping;
    private int _executeStarted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Volatile.Write(ref _executeStarted, 1);
        while (Volatile.Read(ref _stopping) == 0
               && !await TryPublishAsync(
                       AgentPresenceState.Started,
                       sequence: 1,
                       stoppingToken)
                   .ConfigureAwait(false))
        {
            await Task.Delay(options.HeartbeatInterval, stoppingToken).ConfigureAwait(false);
        }

        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        try
        {
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
        if (Volatile.Read(ref _executeStarted) == 1
            && Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _startedConfirmed) == 1)
                {
                    var sequence = checked(Volatile.Read(ref _sequence) + 1);
                    if (await TryPublishCoreAsync(
                            AgentPresenceState.Stopping,
                            sequence,
                            cancellationToken)
                        .ConfigureAwait(false))
                    {
                        Volatile.Write(ref _sequence, sequence);
                    }
                }
            }
            finally
            {
                _publishGate.Release();
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _publishGate.Dispose();
        base.Dispose();
    }

    private int _startedConfirmed;

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

            var sequence = checked(Volatile.Read(ref _sequence) + 1);
            if (await TryPublishCoreAsync(state, sequence, cancellationToken)
                .ConfigureAwait(false))
            {
                Volatile.Write(ref _sequence, sequence);
            }
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async ValueTask<bool> TryPublishAsync(
        AgentPresenceState state,
        long sequence,
        CancellationToken cancellationToken)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var published = await TryPublishCoreAsync(state, sequence, cancellationToken)
                .ConfigureAwait(false);
            if (published && state == AgentPresenceState.Started)
            {
                Volatile.Write(ref _sequence, sequence);
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
        AgentPresenceState state,
        long sequence,
        CancellationToken cancellationToken)
    {
        var message = new AgentPresenceReported(
            options.AgentId,
            options.StationId,
            options.StationSystemId,
            _sessionId,
            sequence,
            state,
            clock.UtcNow);
        AgentPresenceContract.Validate(message);
        try
        {
            await publisher.PublishAsync(
                    nameof(AgentPresenceReported),
                    JsonSerializer.Serialize(message, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            LogPublished(logger, state, sequence, null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogPublishFailed(logger, state, sequence, exception);
            return false;
        }
    }
}
