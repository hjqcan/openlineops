using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class RabbitMqStationCoordinatorTransport :
    IStationJobOutboxPublisher,
    IAsyncDisposable
{
    private readonly StationCoordinatorTransportOptions _options;
    private readonly ConnectionFactory _factory;
    private readonly IStationCoordinatorConfirmedPublicationTransport? _publicationTransport;
    private readonly StationResultDeliveryProcessor _resultProcessor;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publisherChannel;
    private IChannel? _inboxChannel;

    public RabbitMqStationCoordinatorTransport(
        StationCoordinatorTransportOptions options,
        IStationJobCoordinationStore store,
        IStationCoordinatorConfirmedPublicationTransport? publicationTransport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ValidateName(options.CoordinatorId, nameof(options.CoordinatorId));
        ValidateName(options.JobExchange, nameof(options.JobExchange));
        ValidateName(options.EventExchange, nameof(options.EventExchange));
        _options = options;
        _publicationTransport = publicationTransport;
        _resultProcessor = new StationResultDeliveryProcessor(store);
        _factory = new ConnectionFactory
        {
            Uri = options.ResolveBrokerUri(),
            ClientProvidedName = $"OpenLineOps.Coordinator/{options.CoordinatorId}",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };
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
        ArgumentNullException.ThrowIfNull(materialArrivalHandler);
        ArgumentNullException.ThrowIfNull(recoveryRequiredHandler);
        var channel = await GetInboxChannelAsync(cancellationToken).ConfigureAwait(false);
        var settlement = new RabbitMqSettlement(channel);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => _resultProcessor.ProcessAsync(
                ToDelivery(delivery),
                settlement,
                materialArrivalHandler,
                recoveryRequiredHandler,
                cancellationToken)
            .AsTask();
        await channel.BasicConsumeAsync(
                ResultQueueName(),
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer,
                cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_inboxChannel is not null)
        {
            await _inboxChannel.DisposeAsync().ConfigureAwait(false);
        }

        if (_publisherChannel is not null)
        {
            await _publisherChannel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _publishGate.Dispose();
        _connectionGate.Dispose();
    }

    private async ValueTask<IChannel> GetPublisherChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
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
                     nameof(MaterialArrived)
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

    private async ValueTask<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            _connection = await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

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
