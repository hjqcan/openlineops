using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class RabbitMqStationCoordinatorTransport :
    IStationJobOutboxPublisher,
    IAsyncDisposable
{
    private readonly StationCoordinatorTransportOptions _options;
    private readonly ConnectionFactory _publisherFactory;
    private readonly ConnectionFactory _inboxFactory;
    private readonly IStationCoordinatorConfirmedPublicationTransport? _publicationTransport;
    private readonly StationResultDeliveryProcessor _resultProcessor;
    private readonly SemaphoreSlim _publisherConnectionGate = new(1, 1);
    private readonly SemaphoreSlim _inboxConnectionGate = new(1, 1);
    private readonly SemaphoreSlim _inboxConsumerGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private IConnection? _publisherConnection;
    private IConnection? _inboxConnection;
    private IChannel? _publisherChannel;
    private IChannel? _inboxChannel;
    private string? _inboxConsumerTag;
    private int _disposed;

    public RabbitMqStationCoordinatorTransport(
        StationCoordinatorTransportOptions options,
        IStationJobCoordinationStore store,
        IAgentPresenceRepository presenceRepository,
        IStationArtifactReceiptVerifier artifactReceiptVerifier,
        IClock clock,
        IStationCoordinatorConfirmedPublicationTransport? publicationTransport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(presenceRepository);
        ArgumentNullException.ThrowIfNull(artifactReceiptVerifier);
        ArgumentNullException.ThrowIfNull(clock);
        ValidateName(options.CoordinatorId, nameof(options.CoordinatorId));
        ValidateName(options.JobExchange, nameof(options.JobExchange));
        ValidateName(options.EventExchange, nameof(options.EventExchange));
        _options = options;
        _publicationTransport = publicationTransport;
        _resultProcessor = new StationResultDeliveryProcessor(
            store,
            presenceRepository,
            artifactReceiptVerifier,
            options,
            clock);
        _publisherFactory = CreateFactory(
            options,
            $"OpenLineOps.Coordinator.Publisher/{options.CoordinatorId}");
        _inboxFactory = CreateFactory(
            options,
            $"OpenLineOps.Coordinator.Inbox/{options.CoordinatorId}");
    }

    public async ValueTask PublishAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default)
    {
        var publication = StationCoordinatorPublicationFactory.Create(_options, request);
        await PublishAsync(publication, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishAsync(
        ResourceLeaseChanged change,
        CancellationToken cancellationToken = default)
    {
        var publication = StationCoordinatorPublicationFactory.Create(_options, change);
        await PublishAsync(publication, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishAsync(
        StationCoordinatorPublication publication,
        CancellationToken cancellationToken)
    {
        if (_publicationTransport is not null)
        {
            await _publicationTransport.PublishAsync(publication, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await GetPublisherChannelAsync(cancellationToken).ConfigureAwait(false);
            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                ContentEncoding = "utf-8",
                Type = publication.Type,
                AppId = publication.AppId,
                MessageId = publication.MessageId.ToString("D"),
                CorrelationId = publication.CorrelationId.ToString("D")
            };
            try
            {
                await channel.BasicPublishAsync(
                        publication.Exchange,
                        publication.RoutingKey,
                        mandatory: true,
                        properties,
                        publication.Body,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await InvalidatePublisherChannelAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public async Task RunResultInboxAsync(
        Func<MaterialArrived, CancellationToken, ValueTask> materialArrivalHandler,
        Func<StationJobRecoveryRequired, CancellationToken, ValueTask> recoveryRequiredHandler,
        CancellationToken cancellationToken = default)
    {
        await StartResultInboxAsync(
                materialArrivalHandler,
                recoveryRequiredHandler,
                cancellationToken,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopResultInboxAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task StartResultInboxAsync(
        Func<MaterialArrived, CancellationToken, ValueTask> materialArrivalHandler,
        Func<StationJobRecoveryRequired, CancellationToken, ValueTask> recoveryRequiredHandler,
        CancellationToken processingCancellationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialArrivalHandler);
        ArgumentNullException.ThrowIfNull(recoveryRequiredHandler);
        await _inboxConsumerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inboxConsumerTag is not null)
            {
                throw new InvalidOperationException(
                    "The Station result Inbox consumer is already running.");
            }

            var channel = await GetInboxChannelAsync(cancellationToken).ConfigureAwait(false);
            var settlement = new RabbitMqSettlement(channel);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => _resultProcessor.ProcessAsync(
                    ToDelivery(delivery),
                    settlement,
                    materialArrivalHandler,
                    recoveryRequiredHandler,
                    processingCancellationToken)
                .AsTask();
            _inboxConsumerTag = await channel.BasicConsumeAsync(
                    ResultQueueName(),
                    autoAck: false,
                    consumerTag: string.Empty,
                    noLocal: false,
                    exclusive: false,
                    arguments: null,
                    consumer,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await InvalidateInboxChannelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _inboxConsumerGate.Release();
        }
    }

    public async Task StopResultInboxAsync(CancellationToken cancellationToken = default)
    {
        await _inboxConsumerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _inboxConsumerTag = null;
            await InvalidateInboxChannelAsync().ConfigureAwait(false);
        }
        finally
        {
            _inboxConsumerGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopResultInboxAsync(CancellationToken.None).ConfigureAwait(false);

        if (_publisherChannel is not null)
        {
            await _publisherChannel.DisposeAsync().ConfigureAwait(false);
        }

        if (_inboxConnection is not null)
        {
            await _inboxConnection.DisposeAsync().ConfigureAwait(false);
        }

        if (_publisherConnection is not null)
        {
            await _publisherConnection.DisposeAsync().ConfigureAwait(false);
        }

        _publishGate.Dispose();
        _inboxConsumerGate.Dispose();
        _inboxConnectionGate.Dispose();
        _publisherConnectionGate.Dispose();
    }

    private async ValueTask<IChannel> GetPublisherChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await GetPublisherConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (_publisherChannel is { IsOpen: true })
        {
            return _publisherChannel;
        }

        _publisherChannel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken)
            .ConfigureAwait(false);
        await _publisherChannel.ExchangeDeclareAsync(
                _options.JobExchange,
                ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        return _publisherChannel;
    }

    private async ValueTask<IChannel> GetInboxChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await GetInboxConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (_inboxChannel is { IsOpen: true })
        {
            return _inboxChannel;
        }

        _inboxChannel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false),
                cancellationToken)
            .ConfigureAwait(false);
        await _inboxChannel.ExchangeDeclareAsync(
                _options.EventExchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _inboxChannel.QueueDeclareAsync(
                ResultQueueName(),
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var kind in new[]
                 {
                     nameof(StationJobAccepted),
                     nameof(StationJobProgressed),
                     nameof(StationJobCompleted),
                     nameof(StationJobRecoveryRequired),
                     nameof(MaterialArrived),
                     nameof(AgentPresenceReported)
                 })
        {
            await _inboxChannel.QueueBindAsync(
                    ResultQueueName(),
                    _options.EventExchange,
                    StationTransportRoute.EventPattern(kind),
                    arguments: null,
                    noWait: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        await _inboxChannel.BasicQosAsync(0, 16, global: false, cancellationToken)
            .ConfigureAwait(false);
        return _inboxChannel;
    }

    private async ValueTask InvalidatePublisherChannelAsync()
    {
        var channel = Interlocked.Exchange(ref _publisherChannel, null);
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask InvalidateInboxChannelAsync()
    {
        var channel = Interlocked.Exchange(ref _inboxChannel, null);
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<IConnection> GetPublisherConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (_publisherConnection is { IsOpen: true })
        {
            return _publisherConnection;
        }

        await _publisherConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_publisherConnection is { IsOpen: true })
            {
                return _publisherConnection;
            }

            if (_publisherConnection is not null)
            {
                await _publisherConnection.DisposeAsync().ConfigureAwait(false);
            }

            _publisherConnection = await _publisherFactory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return _publisherConnection;
        }
        finally
        {
            _publisherConnectionGate.Release();
        }
    }

    private async ValueTask<IConnection> GetInboxConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (_inboxConnection is { IsOpen: true })
        {
            return _inboxConnection;
        }

        await _inboxConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inboxConnection is { IsOpen: true })
            {
                return _inboxConnection;
            }

            if (_inboxConnection is not null)
            {
                await _inboxConnection.DisposeAsync().ConfigureAwait(false);
            }

            _inboxConnection = await _inboxFactory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return _inboxConnection;
        }
        finally
        {
            _inboxConnectionGate.Release();
        }
    }

    private static ConnectionFactory CreateFactory(
        StationCoordinatorTransportOptions options,
        string clientProvidedName) => new()
        {
            Uri = options.ResolveBrokerUri(),
            ClientProvidedName = clientProvidedName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };

    private string ResultQueueName() =>
        $"openlineops.coordinator.{_options.CoordinatorId}.station-results";

    private static StationCoordinatorTransportDelivery ToDelivery(
        BasicDeliverEventArgs delivery) => new(
        delivery.DeliveryTag,
        delivery.BasicProperties.ContentType,
        delivery.BasicProperties.ContentEncoding,
        delivery.BasicProperties.Type,
        delivery.BasicProperties.AppId,
        delivery.BasicProperties.MessageId,
        delivery.BasicProperties.CorrelationId,
        delivery.RoutingKey,
        delivery.Redelivered,
        delivery.Body);

    private static void ValidateName(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new InvalidOperationException($"Station transport {name} must be canonical text.");
        }
    }

    private sealed class RabbitMqSettlement(IChannel channel)
        : IStationCoordinatorTransportSettlement
    {
        public ValueTask AcknowledgeAsync(
            ulong deliveryTag,
            CancellationToken cancellationToken = default) => channel.BasicAckAsync(
                deliveryTag,
                multiple: false,
                cancellationToken);

        public ValueTask RejectAsync(
            ulong deliveryTag,
            bool requeue,
            CancellationToken cancellationToken = default) => channel.BasicNackAsync(
                deliveryTag,
                multiple: false,
                requeue,
                cancellationToken);
    }
}
