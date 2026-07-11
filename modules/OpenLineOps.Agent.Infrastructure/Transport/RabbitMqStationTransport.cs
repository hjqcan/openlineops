using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly RabbitMqStationTransportOptions _options;
    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publisherChannel;
    private IChannel? _receiverChannel;

    public RabbitMqStationTransport(RabbitMqStationTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BrokerUri);
        _ = Required(options.AgentId, nameof(options.AgentId));
        _ = Required(options.StationId, nameof(options.StationId));
        _ = Required(options.JobExchange, nameof(options.JobExchange));
        _ = Required(options.EventExchange, nameof(options.EventExchange));
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
        _ = Required(kind, nameof(kind));
        _ = Required(payloadJson, nameof(payloadJson));
        using (JsonDocument.Parse(payloadJson))
        {
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
                Type = kind,
                AppId = _options.AgentId
            };
            await channel.BasicPublishAsync(
                    _options.EventExchange,
                    $"station.{_options.StationId}.{kind}",
                    mandatory: true,
                    properties,
                    Encoding.UTF8.GetBytes(payloadJson),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public async Task RunAsync(
        Func<StationJobRequested, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var channel = await GetReceiverChannelAsync(cancellationToken).ConfigureAwait(false);
        var queueName = QueueName();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                if (!string.Equals(
                        delivery.BasicProperties.ContentType,
                        "application/json",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Station job message content type must be application/json.");
                }

                var request = JsonSerializer.Deserialize<StationJobRequested>(
                    delivery.Body.Span,
                    JsonOptions)
                    ?? throw new InvalidDataException("Station job message is null.");
                await handler(request, cancellationToken).ConfigureAwait(false);
                await channel.BasicAckAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException
                                               or InvalidDataException
                                               or ArgumentException)
            {
                await channel.BasicRejectAsync(
                        delivery.DeliveryTag,
                        requeue: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await channel.BasicNackAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        requeue: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                await channel.BasicNackAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        requeue: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        };

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
                $"station.{_options.StationId}",
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

    private string QueueName() => $"openlineops.station.{_options.StationId}.jobs";

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException($"{parameterName} must be canonical non-empty text.", parameterName)
            : value;
}
