using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    public async Task BrokerFailureLogNeverExposesCredentials()
    {
        const string brokerUri =
            "amqps://presence-user:presence-password@rabbitmq.local:5671/production";
        const string authorizationSecret = "presence-authorization-secret";
        var logger = new CapturingPresenceLogger();
        var publisher = new RecoveringPublisher(
            failuresBeforeRecovery: 1,
            $"BrokerUri={brokerUri}; Authorization: Bearer {authorizationSecret}");
        using var worker = CreateWorker(publisher, logger: logger);

        await worker.StartAsync(CancellationToken.None);
        var diagnostic = await logger.FailureDiagnostic.Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        await publisher.HeartbeatPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StopAsync(stop.Token);

        Assert.Contains("IOException", diagnostic, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("presence-user", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("presence-password", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(authorizationSecret, diagnostic, StringComparison.Ordinal);
    }

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
    public async Task StopDuringRejectedStartedAttemptDoesNotPublishOrphanStopping()
    {
        var publisher = new ControlledRejectedStartedPublisher();
        using var worker = CreateWorker(publisher);

        await worker.StartAsync(CancellationToken.None);
        await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stopping = worker.StopAsync(stop.Token);
        Assert.False(stopping.IsCompleted);
        publisher.RejectAttempt();
        await stopping;

        var started = Assert.Single(publisher.Messages);
        Assert.Equal(AgentPresenceState.Started, started.State);
        Assert.Equal(1, started.Sequence);
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
    public async Task StopWaitsForWorkerQuiescenceBeforePublishingStopping()
    {
        var publisher = new RecoveringPublisher(failuresBeforeRecovery: 0);
        var shutdownState = new StationAgentShutdownState();
        using var worker = CreateWorker(
            publisher,
            workerState: StationAgentWorkerState.Running,
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
    public async Task ConcurrentStopCallersShareOneStoppingPublication()
    {
        var publisher = new ControlledStoppingPublisher();
        using var worker = CreateWorker(publisher);

        await worker.StartAsync(CancellationToken.None);
        await publisher.HeartbeatPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var firstStop = worker.StopAsync(stop.Token);
        await publisher.StoppingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondStop = worker.StopAsync(stop.Token);

        Assert.False(firstStop.IsCompleted);
        Assert.False(secondStop.IsCompleted);
        Assert.Equal(1, publisher.StoppingAttempts);

        publisher.ConfirmStopping();
        await Task.WhenAll(firstStop, secondStop);

        Assert.Equal(1, publisher.StoppingAttempts);
        Assert.Single(
            publisher.Messages,
            static message => message.State == AgentPresenceState.Stopping);
    }

    [Fact]
    public async Task ValidStopAfterCanceledAttemptRetriesSameStoppingIdentity()
    {
        var publisher = new CancellationThenConfirmStoppingPublisher();
        using var worker = CreateWorker(publisher);

        await worker.StartAsync(CancellationToken.None);
        await publisher.HeartbeatPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var canceledAttempt = new CancellationTokenSource();
        using var validStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var firstStop = worker.StopAsync(canceledAttempt.Token);
        await publisher.FirstStoppingAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));

        canceledAttempt.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStop);
        await worker.StopAsync(validStop.Token);

        var attempts = publisher.Messages
            .Where(static message => message.State == AgentPresenceState.Stopping)
            .ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.All(attempts, message => Assert.Equal(attempts[0], message));
        Assert.Single(attempts.Select(AgentPresenceContract.MessageId).Distinct());
    }

    [Fact]
    public async Task DisposeDuringConcurrentStopWaitsForPublishGateRelease()
    {
        var publisher = new CancellationDrainPublisher();
        using var worker = CreateWorker(publisher);

        await worker.StartAsync(CancellationToken.None);
        await publisher.HeartbeatStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var executeTask = worker.ExecuteTask
            ?? throw new InvalidOperationException("Presence Execute task was not started.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stopping = worker.StopAsync(stop.Token);
        await publisher.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        worker.Dispose();
        Assert.False(executeTask.IsCompleted);
        publisher.ReleaseCancellationDrain();

        await stopping;
        await executeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(executeTask.IsCompletedSuccessfully);
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
            "Synthetic Stopping confirmation outage.",
            exception.InnerException?.Message);
        Assert.Equal(
            3,
            publisher.Messages.Count(static message =>
                message.State == AgentPresenceState.Stopping));
    }

    private static StationAgentPresenceWorker CreateWorker(
        IStationAgentMessagePublisher publisher,
        IClock? clock = null,
        StationAgentWorkerState workerState = StationAgentWorkerState.Quiesced,
        StationAgentShutdownState? shutdownState = null,
        ILogger<StationAgentPresenceWorker>? logger = null)
    {
        shutdownState ??= new StationAgentShutdownState();
        switch (workerState)
        {
            case StationAgentWorkerState.NotStarted:
                break;
            case StationAgentWorkerState.Running:
                Assert.True(shutdownState.TryMarkWorkerRunning());
                break;
            case StationAgentWorkerState.Quiesced:
                shutdownState.MarkWorkerQuiesced();
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(workerState),
                    workerState,
                    "Unknown Station Agent worker state.");
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
            logger ?? NullLogger<StationAgentPresenceWorker>.Instance);
    }

    private sealed class RecoveringPublisher(
        int failuresBeforeRecovery,
        string failureMessage = "Synthetic broker outage.") :
        IStationAgentMessagePublisher
    {
        private int _attempts;

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
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= failuresBeforeRecovery)
            {
                return ValueTask.FromException(new IOException(failureMessage));
            }

            if (message.State == AgentPresenceState.Heartbeat)
            {
                HeartbeatPublished.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingPresenceLogger :
        ILogger<StationAgentPresenceWorker>
    {
        public TaskCompletionSource<string> FailureDiagnostic { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1102)
            {
                FailureDiagnostic.TrySetResult(
                    $"{formatter(state, exception)} {exception}");
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
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

    private sealed class ControlledStoppingPublisher :
        IStationAgentMessagePublisher
    {
        private readonly TaskCompletionSource _stoppingConfirmation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stoppingAttempts;

        public ConcurrentQueue<AgentPresenceReported> Messages { get; } = new();

        public TaskCompletionSource HeartbeatPublished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StoppingStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int StoppingAttempts => Volatile.Read(ref _stoppingAttempts);

        public void ConfirmStopping() => _stoppingConfirmation.TrySetResult();

        public async ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = DeserializePresence(kind, payloadJson);
            Messages.Enqueue(message);
            if (message.State == AgentPresenceState.Heartbeat)
            {
                HeartbeatPublished.TrySetResult();
                return;
            }

            if (message.State != AgentPresenceState.Stopping)
            {
                return;
            }

            Interlocked.Increment(ref _stoppingAttempts);
            StoppingStarted.TrySetResult();
            await _stoppingConfirmation.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CancellationThenConfirmStoppingPublisher :
        IStationAgentMessagePublisher
    {
        private int _stoppingAttempts;

        public ConcurrentQueue<AgentPresenceReported> Messages { get; } = new();

        public TaskCompletionSource HeartbeatPublished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstStoppingAttempt { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = DeserializePresence(kind, payloadJson);
            Messages.Enqueue(message);
            if (message.State == AgentPresenceState.Heartbeat)
            {
                HeartbeatPublished.TrySetResult();
                return;
            }

            if (message.State != AgentPresenceState.Stopping
                || Interlocked.Increment(ref _stoppingAttempts) != 1)
            {
                return;
            }

            FirstStoppingAttempt.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CancellationDrainPublisher :
        IStationAgentMessagePublisher
    {
        private readonly TaskCompletionSource _releaseCancellationDrain = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HeartbeatStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseCancellationDrain() =>
            _releaseCancellationDrain.TrySetResult();

        public async ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            var message = DeserializePresence(kind, payloadJson);
            if (message.State != AgentPresenceState.Heartbeat)
            {
                return;
            }

            HeartbeatStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await _releaseCancellationDrain.Task;
                throw;
            }
        }
    }

    private sealed class CancellationBoundStartedPublisher :
        IStationAgentMessagePublisher
    {
        private readonly TaskCompletionSource _publication = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

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
            using var registration = cancellationToken.Register(
                () => _publication.TrySetCanceled(cancellationToken));
            await _publication.Task.ConfigureAwait(false);
        }
    }

    private sealed class ControlledRejectedStartedPublisher :
        IStationAgentMessagePublisher
    {
        private readonly TaskCompletionSource _rejectAttempt = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<AgentPresenceReported> Messages { get; } = new();

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void RejectAttempt() => _rejectAttempt.TrySetResult();

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
            await _rejectAttempt.Task.ConfigureAwait(false);
            throw new IOException("Synthetic broker rejection.");
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

    private static AgentPresenceReported DeserializePresence(
        string kind,
        string payloadJson)
    {
        Assert.Equal(nameof(AgentPresenceReported), kind);
        return JsonSerializer.Deserialize<AgentPresenceReported>(
            payloadJson,
            JsonOptions)
            ?? throw new InvalidDataException("Presence test payload is null.");
    }
}
