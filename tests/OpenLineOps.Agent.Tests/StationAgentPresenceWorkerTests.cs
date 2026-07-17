using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentPresenceWorkerTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartupPublishFailureRetriesStartedBeforeHeartbeatAndGracefulStopping()
    {
        var publisher = new RecoveringPublisher(failuresBeforeRecovery: 2);
        using var worker = CreateWorker(publisher, new IncrementingClock(Now));

        await worker.StartAsync(CancellationToken.None);
        await publisher.HeartbeatPublished.Task.WaitAsync(TimeSpan.FromSeconds(4));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StopAsync(stop.Token);

        var messages = publisher.Messages.ToArray();
        Assert.True(messages.Length >= 5);
        Assert.All(messages[..3], message =>
        {
            Assert.Equal(AgentPresenceState.Started, message.State);
            Assert.Equal(1, message.Sequence);
        });
        Assert.All(messages[..3], message => Assert.Equal(messages[0], message));
        Assert.Single(messages[..3]
            .Select(AgentPresenceContract.MessageId)
            .Distinct());
        Assert.Single(messages[..3].Select(static message => message.SessionId).Distinct());
        var firstHeartbeat = messages.First(static message =>
            message.State == AgentPresenceState.Heartbeat);
        Assert.Equal(2, firstHeartbeat.Sequence);
        var stopping = Assert.Single(messages, static message =>
            message.State == AgentPresenceState.Stopping);
        Assert.True(stopping.Sequence > firstHeartbeat.Sequence);
        Assert.Equal(messages[0].SessionId, stopping.SessionId);
    }

    [Fact]
    public async Task StopBeforeStartedConfirmationDoesNotPublishOrphanStopping()
    {
        var publisher = new RecoveringPublisher(int.MaxValue);
        using var worker = CreateWorker(publisher);

        await worker.StartAsync(CancellationToken.None);
        await publisher.ThirdAttempt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StopAsync(stop.Token);

        Assert.NotEmpty(publisher.Messages);
        Assert.All(publisher.Messages, message =>
            Assert.Equal(AgentPresenceState.Started, message.State));
    }

    [Fact]
    public async Task StopCancelsInFlightHeartbeatBeforePublishingStopping()
    {
        var publisher = new CancellationBoundHeartbeatPublisher();
        using var worker = CreateWorker(publisher);

        await worker.StartAsync(CancellationToken.None);
        await publisher.HeartbeatStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StopAsync(stop.Token);

        var messages = publisher.Messages.ToArray();
        Assert.Equal(
            [
                AgentPresenceState.Started,
                AgentPresenceState.Heartbeat,
                AgentPresenceState.Stopping
            ],
            messages.Select(static message => message.State));
        var heartbeat = messages[1];
        var stopping = messages[2];
        Assert.True(stopping.Sequence > heartbeat.Sequence);
        Assert.NotEqual(
            AgentPresenceContract.MessageId(heartbeat),
            AgentPresenceContract.MessageId(stopping));
        Assert.Equal([1, 2, 3], messages.Select(static message => message.Sequence));
        Assert.Equal(
            3,
            messages.Select(AgentPresenceContract.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task StopCancelsInFlightStartedWithoutPublishingOrphanStopping()
    {
        var publisher = new CancellationBoundStartedPublisher();
        using var worker = CreateWorker(publisher);

        await worker.StartAsync(CancellationToken.None);
        await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StopAsync(stop.Token);

        var started = Assert.Single(publisher.Messages);
        Assert.Equal(AgentPresenceState.Started, started.State);
        Assert.Equal(1, started.Sequence);
    }

    [Fact]
    public async Task StopWaitsForWorkerQuiescenceBeforePublishingStopping()
    {
        var publisher = new RecoveringPublisher(failuresBeforeRecovery: 0);
        var shutdownState = new StationAgentShutdownState();
        using var worker = CreateWorker(
            publisher,
            workerQuiesced: false,
            shutdownState: shutdownState);

        await worker.StartAsync(CancellationToken.None);
        await publisher.HeartbeatPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stopping = worker.StopAsync(stop.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.False(stopping.IsCompleted);
        Assert.DoesNotContain(
            publisher.Messages,
            static message => message.State == AgentPresenceState.Stopping);

        shutdownState.MarkWorkerQuiesced();
        await stopping;
        Assert.Single(
            publisher.Messages,
            static message => message.State == AgentPresenceState.Stopping);
    }

    [Fact]
    public async Task StoppingRetriesTheSameMessageUntilBrokerConfirmation()
    {
        var publisher = new StoppingRetryPublisher(stoppingFailures: 2);
        using var worker = CreateWorker(publisher, new IncrementingClock(Now));

        await worker.StartAsync(CancellationToken.None);
        await publisher.HeartbeatPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StopAsync(stop.Token);

        var attempts = publisher.Messages
            .Where(static message => message.State == AgentPresenceState.Stopping)
            .ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.All(attempts, message => Assert.Equal(attempts[0], message));
        Assert.Single(attempts.Select(AgentPresenceContract.MessageId).Distinct());
    }

    [Fact]
    public async Task UnconfirmedStoppingFailsHostShutdownAfterBoundedRetries()
    {
        var publisher = new StoppingRetryPublisher(stoppingFailures: int.MaxValue);
        using var worker = CreateWorker(publisher);

        await worker.StartAsync(CancellationToken.None);
        await publisher.HeartbeatPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var exception = await Assert.ThrowsAsync<IOException>(
            () => worker.StopAsync(stop.Token));

        Assert.Contains("3 attempts", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            3,
            publisher.Messages.Count(static message =>
                message.State == AgentPresenceState.Stopping));
    }

    private static StationAgentPresenceWorker CreateWorker(
        IStationAgentMessagePublisher publisher,
        IClock? clock = null,
        bool workerQuiesced = true,
        StationAgentShutdownState? shutdownState = null)
    {
        shutdownState ??= new StationAgentShutdownState();
        if (workerQuiesced)
        {
            shutdownState.MarkWorkerQuiesced();
        }

        return new StationAgentPresenceWorker(
            new StationAgentPresenceOptions(
                "agent.main",
                "station.main",
                "station-system.main",
                TimeSpan.FromMilliseconds(250)),
            publisher,
            clock ?? new FixedClock(Now),
            shutdownState,
            NullLogger<StationAgentPresenceWorker>.Instance);
    }

    private sealed class RecoveringPublisher(int failuresBeforeRecovery) :
        IStationAgentMessagePublisher
    {
        private int _attempts;

        public ConcurrentQueue<AgentPresenceReported> Messages { get; } = new();

        public TaskCompletionSource HeartbeatPublished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ThirdAttempt { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(nameof(AgentPresenceReported), kind);
            var message = JsonSerializer.Deserialize<AgentPresenceReported>(
                payloadJson,
                JsonOptions)
                ?? throw new InvalidDataException("Presence test payload is null.");
            Messages.Enqueue(message);
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt >= 3)
            {
                ThirdAttempt.TrySetResult();
            }

            if (attempt <= failuresBeforeRecovery)
            {
                return ValueTask.FromException(new IOException("Synthetic broker outage."));
            }

            if (message.State == AgentPresenceState.Heartbeat)
            {
                HeartbeatPublished.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationBoundHeartbeatPublisher :
        IStationAgentMessagePublisher
    {
        public ConcurrentQueue<AgentPresenceReported> Messages { get; } = new();

        public TaskCompletionSource HeartbeatStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(nameof(AgentPresenceReported), kind);
            var message = JsonSerializer.Deserialize<AgentPresenceReported>(
                payloadJson,
                JsonOptions)
                ?? throw new InvalidDataException("Presence test payload is null.");
            Messages.Enqueue(message);
            if (message.State != AgentPresenceState.Heartbeat)
            {
                return;
            }

            HeartbeatStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class StoppingRetryPublisher(int stoppingFailures) :
        IStationAgentMessagePublisher
    {
        private int _stoppingAttempts;

        public ConcurrentQueue<AgentPresenceReported> Messages { get; } = new();

        public TaskCompletionSource HeartbeatPublished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(nameof(AgentPresenceReported), kind);
            var message = JsonSerializer.Deserialize<AgentPresenceReported>(
                payloadJson,
                JsonOptions)
                ?? throw new InvalidDataException("Presence test payload is null.");
            Messages.Enqueue(message);
            if (message.State == AgentPresenceState.Heartbeat)
            {
                HeartbeatPublished.TrySetResult();
            }

            if (message.State == AgentPresenceState.Stopping
                && Interlocked.Increment(ref _stoppingAttempts) <= stoppingFailures)
            {
                return ValueTask.FromException(
                    new IOException("Synthetic Stopping confirmation outage."));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationBoundStartedPublisher :
        IStationAgentMessagePublisher
    {
        public ConcurrentQueue<AgentPresenceReported> Messages { get; } = new();

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(nameof(AgentPresenceReported), kind);
            var message = JsonSerializer.Deserialize<AgentPresenceReported>(
                payloadJson,
                JsonOptions)
                ?? throw new InvalidDataException("Presence test payload is null.");
            Messages.Enqueue(message);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class IncrementingClock(DateTimeOffset firstUtc) : IClock
    {
        private long _reads;

        public DateTimeOffset UtcNow => firstUtc.AddSeconds(
            Interlocked.Increment(ref _reads) - 1);
    }
}
