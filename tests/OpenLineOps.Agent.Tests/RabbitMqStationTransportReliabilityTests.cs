using System.Text.Json;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Tests;

public sealed class RabbitMqStationTransportReliabilityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConfirmedPublisherFailureEscapesAndIdenticalRetryKeepsCanonicalIdentity()
    {
        var publications = new FailOncePublicationTransport();
        await using var transport = new RabbitMqStationTransport(JobOptions(), publications);
        var accepted = new StationJobAccepted(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "job/unit-001/operation.main/1",
            "agent.main",
            "station.main",
            Now);
        var payload = JsonSerializer.Serialize(accepted, JsonOptions());

        await Assert.ThrowsAsync<IOException>(async () =>
            await transport.PublishAsync(nameof(StationJobAccepted), payload));
        await transport.PublishAsync(nameof(StationJobAccepted), payload);

        Assert.Equal(2, publications.Attempts);
        Assert.Equal(
            publications.Publications[0].MessageId,
            publications.Publications[1].MessageId);
        Assert.Equal(
            publications.Publications[0].CorrelationId,
            publications.Publications[1].CorrelationId);
        Assert.True(publications.Publications[0].Body.Span.SequenceEqual(
            publications.Publications[1].Body.Span));
        var publication = publications.Publications[1];
        Assert.Equal(accepted.MessageId, publication.MessageId);
        Assert.Equal(accepted.JobId, publication.CorrelationId);
        Assert.Equal(
            "station.station.main.StationJobAccepted",
            publication.RoutingKey);
    }

    [Fact]
    public async Task AckCrashCausesRedeliveryWithoutReplayingDurablyAcceptedJob()
    {
        var request = JobRequest();
        var processor = new StationJobDeliveryProcessor(JobOptions());
        var durableInbox = new HashSet<string>(StringComparer.Ordinal);
        var hardwareExecutions = 0;
        var firstSettlement = new RecordingSettlement(failAcknowledgement: true);

        async ValueTask HandleAsync(
            StationJobRequested message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (durableInbox.Add(message.IdempotencyKey))
            {
                hardwareExecutions++;
            }

            await ValueTask.CompletedTask;
        }

        await Assert.ThrowsAsync<IOException>(async () =>
            await processor.ProcessAsync(
                Delivery(request, deliveryTag: 1, redelivered: false),
                HandleAsync,
                IgnoreResourceLeaseAsync,
                firstSettlement));

        var redeliverySettlement = new RecordingSettlement();
        await processor.ProcessAsync(
            Delivery(request, deliveryTag: 2, redelivered: true),
            HandleAsync,
            IgnoreResourceLeaseAsync,
            redeliverySettlement);

        Assert.Equal(1, hardwareExecutions);
        Assert.Equal([2UL], redeliverySettlement.Acknowledged);
        Assert.Empty(redeliverySettlement.Rejected);
    }

    [Fact]
    public async Task WrongJobEnvelopeIdentityIsRejectedBeforeInboxHandler()
    {
        var request = JobRequest();
        var delivery = Delivery(request, deliveryTag: 3, redelivered: false) with
        {
            CorrelationId = Guid.NewGuid().ToString("D")
        };
        var settlement = new RecordingSettlement();
        var handled = false;

        await new StationJobDeliveryProcessor(JobOptions()).ProcessAsync(
            delivery,
            (_, _) =>
            {
                handled = true;
                return ValueTask.CompletedTask;
            },
            IgnoreResourceLeaseAsync,
            settlement);

        Assert.False(handled);
        Assert.Equal([(3UL, false)], settlement.Rejected);
    }

    [Fact]
    public async Task SafetyAckPublishFailureRequeuesAndRedeliveryDoesNotReplayActuator()
    {
        var coordinator = new StationSafetyCommandCoordinator(
            new DurableSafetyInboxStore(),
            new FixedClock(Now));
        var options = SafetyOptions();
        var processor = new StationSafetyDeliveryProcessor(options, coordinator);
        var request = new EmergencyStopRequested(
            Guid.NewGuid(),
            "emergency/station.main/guard-open",
            options.AgentId,
            options.StationId,
            "Guard opened",
            "operator.main",
            Now);
        var actuatorExecutions = 0;
        var publisher = new FailOnceSafetyPublisher();
        var firstSettlement = new RecordingSettlement();

        ValueTask<StationSafetyExecutionResult> ActuateAsync(
            EmergencyStopRequested _,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actuatorExecutions++;
            return ValueTask.FromResult(new StationSafetyExecutionResult(true, null, null));
        }

        await processor.ProcessEmergencyStopAsync(
            SafetyDelivery(request, 10, redelivered: false),
            ActuateAsync,
            publisher,
            firstSettlement);
        Assert.Equal([(10UL, true)], firstSettlement.Rejected);

        var redeliverySettlement = new RecordingSettlement();
        await processor.ProcessEmergencyStopAsync(
            SafetyDelivery(request, 11, redelivered: true),
            ActuateAsync,
            publisher,
            redeliverySettlement);

        Assert.Equal(1, actuatorExecutions);
        Assert.Equal(2, publisher.Attempts);
        Assert.Equal([11UL], redeliverySettlement.Acknowledged);
        Assert.Equal(
            publisher.Publications[0].MessageId,
            publisher.Publications[1].MessageId);
    }

    [Fact]
    public async Task EmergencyChannelRunsWhileControlChannelHandlerIsBlocked()
    {
        var supervisor = new StationSafetyChannelSupervisor();
        var controlStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var emergencyCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(
            async cancellationToken =>
            {
                emergencyCompleted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            async cancellationToken =>
            {
                controlStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            stop.Token);

        await controlStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await emergencyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(running.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await running.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static RabbitMqStationTransportOptions JobOptions() => new(
        new Uri("amqp://localhost"),
        "agent.main",
        "station.main",
        RequireTls: false);

    private static RabbitMqStationSafetyOptions SafetyOptions() => new(
        new Uri("amqp://localhost"),
        "agent.main",
        "station.main",
        RequireTls: false);

    private static StationJobRequested JobRequest()
    {
        using var inputs = JsonDocument.Parse("{}");
        return new StationJobRequested(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "job/unit-001/operation.main/1",
            "agent.main",
            "station.main",
            "station-system.main",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "operation.main@0001",
            1,
            "product.board",
            "serialNumber",
            "UNIT-001",
            null,
            null,
            "project.main",
            "application.main",
            "snapshot.main",
            "line.main",
            "topology.main",
            "operator.main",
            new string('a', 64),
            "operation.main",
            "flow.main",
            "flow-version.main",
            "configuration.main",
            "recipe.main",
            [],
            inputs.RootElement.Clone(),
            Now);
    }

    private static StationTransportDelivery Delivery(
        StationJobRequested request,
        ulong deliveryTag,
        bool redelivered) => new(
        deliveryTag,
        "application/json",
        "utf-8",
        nameof(StationJobRequested),
        "coordinator.main",
        request.MessageId.ToString("D"),
        request.JobId.ToString("D"),
        $"station.{request.StationId}",
        redelivered,
        JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions()));

    private static StationTransportDelivery SafetyDelivery(
        EmergencyStopRequested request,
        ulong deliveryTag,
        bool redelivered) => new(
        deliveryTag,
        "application/json",
        "utf-8",
        nameof(EmergencyStopRequested),
        "coordinator.main",
        request.MessageId.ToString("D"),
        request.MessageId.ToString("D"),
        $"station.{request.StationId}.emergency-stop",
        redelivered,
        JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions()));

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private static ValueTask IgnoreResourceLeaseAsync(
        ResourceLeaseChanged _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private sealed class FailOncePublicationTransport :
        IStationAgentConfirmedPublicationTransport
    {
        public int Attempts { get; private set; }

        public List<StationAgentEventPublication> Publications { get; } = [];

        public ValueTask PublishAsync(
            StationAgentEventPublication publication,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            Publications.Add(publication);
            return Attempts == 1
                ? ValueTask.FromException(new IOException("Broker confirm failed."))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceSafetyPublisher : IStationSafetyAcknowledgementPublisher
    {
        public int Attempts { get; private set; }

        public List<StationAgentEventPublication> Publications { get; } = [];

        public ValueTask PublishAsync(
            StationAgentEventPublication publication,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            Publications.Add(publication);
            return Attempts == 1
                ? ValueTask.FromException(new IOException("Mandatory publication returned."))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSettlement(bool failAcknowledgement = false)
        : IStationTransportSettlement
    {
        public List<ulong> Acknowledged { get; } = [];

        public List<(ulong DeliveryTag, bool Requeue)> Rejected { get; } = [];

        public ValueTask AcknowledgeAsync(
            ulong deliveryTag,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failAcknowledgement)
            {
                return ValueTask.FromException(new IOException("Connection closed before ack."));
            }

            Acknowledged.Add(deliveryTag);
            return ValueTask.CompletedTask;
        }

        public ValueTask RejectAsync(
            ulong deliveryTag,
            bool requeue,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rejected.Add((deliveryTag, requeue));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow => value;
    }

    private sealed class DurableSafetyInboxStore : IStationSafetyInboxStore
    {
        private readonly Dictionary<string, StationSafetyInboxEntry> _entries =
            new(StringComparer.Ordinal);

        public ValueTask<StationSafetyInboxEntry?> GetAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _entries.TryGetValue(idempotencyKey, out var entry) ? entry : null);
        }

        public ValueTask<StationSafetyInboxEntry?> GetJobCancellationAsync(
            OpenLineOps.Agent.Domain.StationJobs.StationJobId jobId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_entries.Values.SingleOrDefault(entry =>
                entry.TargetJobId == jobId.Value));
        }

        public ValueTask<bool> TryBeginAsync(
            StationSafetyInboxEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.ContainsKey(entry.IdempotencyKey))
            {
                return ValueTask.FromResult(false);
            }

            _entries.Add(entry.IdempotencyKey, entry);
            return ValueTask.FromResult(true);
        }

        public ValueTask<StationSafetyInboxEntry> CompleteAsync(
            string idempotencyKey,
            StationSafetyCommandKind commandKind,
            string requestSha256,
            string acknowledgementJson,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _entries[idempotencyKey];
            if (current.CommandKind != commandKind
                || !string.Equals(current.RequestSha256, requestSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Safety completion identity mismatch.");
            }

            var completed = current with
            {
                AcknowledgementJson = acknowledgementJson,
                CompletedAtUtc = completedAtUtc
            };
            _entries[idempotencyKey] = completed;
            return ValueTask.FromResult(completed);
        }
    }
}
