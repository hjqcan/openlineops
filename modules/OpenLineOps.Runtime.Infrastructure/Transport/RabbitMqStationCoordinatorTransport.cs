using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly StationCoordinatorTransportOptions _options;
    private readonly IStationJobCoordinationStore _store;
    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publisherChannel;
    private IChannel? _inboxChannel;

    public RabbitMqStationCoordinatorTransport(
        StationCoordinatorTransportOptions options,
        IStationJobCoordinationStore store)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ValidateName(options.CoordinatorId, nameof(options.CoordinatorId));
        ValidateName(options.JobExchange, nameof(options.JobExchange));
        ValidateName(options.EventExchange, nameof(options.EventExchange));
        _options = options;
        _store = store;
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
        ArgumentNullException.ThrowIfNull(request);
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await GetPublisherChannelAsync(cancellationToken).ConfigureAwait(false);
            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                ContentEncoding = "utf-8",
                Type = nameof(StationJobRequested),
                AppId = _options.CoordinatorId,
                MessageId = request.MessageId.ToString("D"),
                CorrelationId = request.JobId.ToString("D")
            };
            await channel.BasicPublishAsync(
                    _options.JobExchange,
                    $"station.{request.StationId}",
                    mandatory: true,
                    properties,
                    JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public async Task RunResultInboxAsync(CancellationToken cancellationToken = default)
    {
        var channel = await GetInboxChannelAsync(cancellationToken).ConfigureAwait(false);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                ValidateDelivery(delivery.BasicProperties);
                switch (delivery.BasicProperties.Type)
                {
                    case nameof(StationJobAccepted):
                        var accepted = JsonSerializer.Deserialize<StationJobAccepted>(
                            delivery.Body.Span,
                            JsonOptions)
                            ?? throw new InvalidDataException("Station accepted message is null.");
                        await _store.RecordAcceptedAsync(accepted, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case nameof(StationJobProgressed):
                        var progress = JsonSerializer.Deserialize<StationJobProgressed>(
                            delivery.Body.Span,
                            JsonOptions)
                            ?? throw new InvalidDataException("Station progress message is null.");
                        await _store.RecordProgressAsync(progress, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case nameof(StationJobCompleted):
                        var completion = JsonSerializer.Deserialize<StationJobCompleted>(
                            delivery.Body.Span,
                            JsonOptions)
                            ?? throw new InvalidDataException("Station completion message is null.");
                        await _store.RecordCompletionAsync(completion, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unsupported Station result message type '{delivery.BasicProperties.Type}'.");
                }
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
                     nameof(StationJobCompleted)
                 })
        {
            await _inboxChannel.QueueBindAsync(
                    ResultQueueName(),
                    _options.EventExchange,
                    $"station.*.{kind}",
                    arguments: null,
                    noWait: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        await _inboxChannel.BasicQosAsync(0, 16, global: false, cancellationToken)
            .ConfigureAwait(false);
        return _inboxChannel;
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

    private static void ValidateDelivery(IReadOnlyBasicProperties properties)
    {
        if (!string.Equals(properties.ContentType, "application/json", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Station result message content type must be application/json.");
        }
    }

    private static void ValidateName(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new InvalidOperationException($"Station transport {name} must be canonical text.");
        }
    }
}
