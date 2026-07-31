using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenLineOps.Integration.Api.Transport;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Application.Serialization;

namespace OpenLineOps.Integration.Api.Tests;

public sealed class IntegrationOutboxHostedServiceTests
{
    [Fact]
    public async Task ReadyConnectorAutomaticallyDrainsPersistedOutbox()
    {
        var now = new DateTimeOffset(
            2026,
            7,
            31,
            1,
            2,
            3,
            TimeSpan.Zero);
        var store = new RecordingOutboxStore(Message(now));
        var connector = new RecordingConnector();
        using var services = Services(store, connector);
        var worker = Worker(services, now);

        await worker.StartAsync(CancellationToken.None);
        await store.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, connector.SendCount);
        Assert.Equal("response-001", store.DeliveredMessageId);
    }

    [Fact]
    public async Task UnavailableConnectorDoesNotConsumeAttemptsOrReadPendingRows()
    {
        var now = new DateTimeOffset(
            2026,
            7,
            31,
            1,
            2,
            3,
            TimeSpan.Zero);
        var store = new RecordingOutboxStore(Message(now));
        var connector = new UnavailableConnector();
        using var services = Services(store, connector);
        var worker = Worker(services, now);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, connector.SendCount);
        Assert.Equal(0, store.ListReadyCount);
        Assert.Equal(0, store.FailureCount);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1001, 10)]
    [InlineData(1, 9)]
    public void UnsafeWorkerBoundsAreRejected(
        int batchSize,
        int pollMilliseconds)
    {
        var options = new IntegrationOutboxWorkerOptions
        {
            BatchSize = batchSize,
            PollInterval = TimeSpan.FromMilliseconds(pollMilliseconds)
        };

        Assert.False(options.IsValid);
    }

    private static ServiceProvider Services(
        IIntegrationOutboxStore store,
        IIntegrationConnector connector)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(connector);
        services.AddScoped<IntegrationOutboxDispatcher>();
        return services.BuildServiceProvider();
    }

    private static IntegrationOutboxDispatcherHostedService Worker(
        IServiceProvider services,
        DateTimeOffset now) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(now),
            Options.Create(new IntegrationOutboxWorkerOptions
            {
                BatchSize = 10,
                PollInterval = TimeSpan.FromMilliseconds(10),
                ConnectorUnavailableInterval = TimeSpan.FromMilliseconds(10),
                FailureInterval = TimeSpan.FromMilliseconds(10)
            }),
            NullLogger<IntegrationOutboxDispatcherHostedService>.Instance);

    private static IntegrationOutboundMessage Message(DateTimeOffset now) =>
        new(
            1,
            "response-001",
            "request-001",
            IntegrationMessageCodec.ComputeSha256("{}"),
            "{}",
            now,
            0,
            now);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingConnector : IIntegrationConnector
    {
        public int SendCount { get; private set; }

        public ValueTask SendAsync(
            IntegrationOutboundMessage message,
            CancellationToken cancellationToken = default)
        {
            _ = message;
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnavailableConnector :
        IIntegrationConnector,
        IIntegrationConnectorReadiness
    {
        public int SendCount { get; private set; }

        public bool IsReady => false;

        public string UnavailabilityReason => "test connector is offline";

        public ValueTask SendAsync(
            IntegrationOutboundMessage message,
            CancellationToken cancellationToken = default)
        {
            _ = message;
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOutboxStore(
        IntegrationOutboundMessage message) : IIntegrationOutboxStore
    {
        private bool _delivered;

        public TaskCompletionSource Delivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ListReadyCount { get; private set; }

        public int FailureCount { get; private set; }

        public string? DeliveredMessageId { get; private set; }

        public ValueTask<IReadOnlyList<IntegrationOutboundMessage>> ListReadyAsync(
            int maximumCount,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            _ = maximumCount;
            _ = nowUtc;
            cancellationToken.ThrowIfCancellationRequested();
            ListReadyCount++;
            return ValueTask.FromResult<IReadOnlyList<IntegrationOutboundMessage>>(
                _delivered ? [] : [message]);
        }

        public ValueTask MarkDeliveredAsync(
            string messageId,
            DateTimeOffset deliveredAtUtc,
            CancellationToken cancellationToken = default)
        {
            _ = deliveredAtUtc;
            cancellationToken.ThrowIfCancellationRequested();
            _delivered = true;
            DeliveredMessageId = messageId;
            Delivered.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordFailureAsync(
            string messageId,
            int expectedAttemptCount,
            string failure,
            DateTimeOffset failedAtUtc,
            DateTimeOffset nextAttemptAtUtc,
            bool deadLetter,
            CancellationToken cancellationToken = default)
        {
            _ = messageId;
            _ = expectedAttemptCount;
            _ = failure;
            _ = failedAtUtc;
            _ = nextAttemptAtUtc;
            _ = deadLetter;
            cancellationToken.ThrowIfCancellationRequested();
            FailureCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask RequeueDeadLetterAsync(
            string messageId,
            string actorId,
            string reason,
            DateTimeOffset replayedAtUtc,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());

        public ValueTask<IReadOnlyList<IntegrationReplayAudit>>
            ListReplayAuditAsync(
                string messageId,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyList<IntegrationReplayAudit>>(
                new NotSupportedException());

        public ValueTask<IReadOnlyList<IntegrationOutboxFailureAudit>>
            ListFailureAuditAsync(
                string messageId,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyList<IntegrationOutboxFailureAudit>>(
                new NotSupportedException());
    }
}
