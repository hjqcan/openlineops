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
        using var worker = CreateWorker(publisher);

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

    private static StationAgentPresenceWorker CreateWorker(
        IStationAgentMessagePublisher publisher) => new(
        new StationAgentPresenceOptions(
            "agent.main",
            "station.main",
            "station-system.main",
            TimeSpan.FromMilliseconds(250)),
        publisher,
        new FixedClock(Now),
        NullLogger<StationAgentPresenceWorker>.Instance);

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

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
