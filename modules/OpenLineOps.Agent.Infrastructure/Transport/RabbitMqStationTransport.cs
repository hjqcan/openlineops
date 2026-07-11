using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed record RabbitMqStationTransportOptions(
    Uri BrokerUri,
    string AgentId,
    string StationId,
    string JobExchange = "openlineops.station.jobs",
    string EventExchange = "openlineops.station.events",
    ushort PrefetchCount = 8,
    ushort MaximumConcurrentJobs = 4,
    bool RequireTls = true);

public sealed class RabbitMqStationTransport :
    IStationJobReceiver,
    IStationAgentMessagePublisher,
    IAsyncDisposable
{
    private readonly RabbitMqStationTransportOptions _options;
    private readonly ConnectionFactory _factory;
    private readonly IStationAgentConfirmedPublicationTransport? _publicationTransport;
    private readonly StationJobDeliveryProcessor _deliveryProcessor;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publisherChannel;
    private IChannel? _receiverChannel;

    public RabbitMqStationTransport(
        RabbitMqStationTransportOptions options,
        IStationAgentConfirmedPublicationTransport? publicationTransport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BrokerUri);
        _ = Required(options.AgentId, nameof(options.AgentId));
        _ = Required(options.StationId, nameof(options.StationId));
        _ = Required(options.JobExchange, nameof(options.JobExchange));
        _ = Required(options.EventExchange, nameof(options.EventExchange));
        if (options.BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new ArgumentException(
                "Station Agent RabbitMQ BrokerUri must use amqp or amqps.",
                nameof(options));
        }

        if (options.PrefetchCount == 0
            || options.MaximumConcurrentJobs == 0
            || options.MaximumConcurrentJobs > options.PrefetchCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Concurrent job count must be positive and cannot exceed prefetch count.");
        }

        if (options.RequireTls
            && !string.Equals(options.BrokerUri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Station Agent RabbitMQ transport requires an amqps URI.",
                nameof(options));
        }

        _options = options;
        _publicationTransport = publicationTransport;
        _deliveryProcessor = new StationJobDeliveryProcessor(options);
        _factory = new ConnectionFactory
        {
            Uri = options.BrokerUri,
            ClientProvidedName = $"OpenLineOps.Agent/{options.AgentId}/{options.StationId}",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = options.MaximumConcurrentJobs
        };
    }

    public async ValueTask PublishAsync(
        string kind,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        var publication = StationAgentEventPublicationFactory.Create(_options, kind, payloadJson);
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

    public async Task RunAsync(
        Func<StationJobRequested, CancellationToken, ValueTask> handler,
        Func<ResourceLeaseChanged, CancellationToken, ValueTask> resourceLeaseHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(resourceLeaseHandler);
        var channel = await GetReceiverChannelAsync(cancellationToken).ConfigureAwait(false);
        var queueName = QueueName();
        var settlement = new RabbitMqSettlement(channel);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => _deliveryProcessor.ProcessAsync(
                ToDelivery(delivery),
                handler,
                resourceLeaseHandler,
                settlement,
                cancellationToken)
            .AsTask();

        await channel.BasicConsumeAsync(
                queueName,
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
        if (_receiverChannel is not null)
        {
            await _receiverChannel.DisposeAsync().ConfigureAwait(false);
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
                _options.EventExchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        return _publisherChannel;
    }

    private async ValueTask InvalidatePublisherChannelAsync()
    {
        var channel = Interlocked.Exchange(ref _publisherChannel, null);
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<IChannel> GetReceiverChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (_receiverChannel is { IsOpen: true })
        {
            return _receiverChannel;
        }

        _receiverChannel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false),
                cancellationToken)
            .ConfigureAwait(false);
        await _receiverChannel.ExchangeDeclareAsync(
                _options.JobExchange,
                ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        var queueName = QueueName();
        await _receiverChannel.QueueDeclareAsync(
                queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _receiverChannel.QueueBindAsync(
                queueName,
                _options.JobExchange,
                $"station.{_options.AgentId}.{_options.StationId}",
                arguments: null,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _receiverChannel.QueueBindAsync(
                queueName,
                _options.JobExchange,
                $"station.{_options.AgentId}.{_options.StationId}.resource-lease-changed",
                arguments: null,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _receiverChannel.BasicQosAsync(
                prefetchSize: 0,
                _options.PrefetchCount,
                global: false,
                cancellationToken)
            .ConfigureAwait(false);
        return _receiverChannel;
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

            _connection = await _factory.CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private string QueueName() =>
        $"openlineops.station.{_options.AgentId}.{_options.StationId}.jobs";

    private static StationTransportDelivery ToDelivery(BasicDeliverEventArgs delivery) => new(
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

    private sealed class RabbitMqSettlement(IChannel channel) : IStationTransportSettlement
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

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException($"{parameterName} must be canonical non-empty text.", parameterName)
            : value;
}
