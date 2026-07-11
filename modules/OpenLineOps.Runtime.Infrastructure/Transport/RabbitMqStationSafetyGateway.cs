using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class RabbitMqStationSafetyGateway : IStationSafetyGateway, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly StationCoordinatorTransportOptions _options;
    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<StationSafeStopAcknowledged>> _pending = [];
    private IConnection? _connection;
    private IChannel? _publisherChannel;
    private IChannel? _acknowledgementChannel;
    private int _started;

    public RabbitMqStationSafetyGateway(StationCoordinatorTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SafetyAcknowledgementTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Station safety acknowledgement timeout must be positive.");
        }

        _options = options;
        _factory = new ConnectionFactory
        {
            Uri = options.ResolveBrokerUri(),
            ClientProvidedName = $"OpenLineOps.Coordinator.Safety/{options.CoordinatorId}",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };
    }

    public async ValueTask<StationSafeStopAcknowledged> RequestSafeStopAsync(
        StationSafeStopRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<StationSafeStopAcknowledged>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.MessageId, completion))
        {
            throw new InvalidOperationException(
                $"Safe Stop request message {request.MessageId:D} is already pending.");
        }

        try
        {
            await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var properties = new BasicProperties
                {
                    Persistent = true,
                    ContentType = "application/json",
                    ContentEncoding = "utf-8",
                    Type = nameof(StationSafeStopRequested),
                    AppId = _options.CoordinatorId,
                    MessageId = request.MessageId.ToString("D"),
                    CorrelationId = request.ProductionRunId.ToString("D"),
                    Priority = 8
                };
                await (_publisherChannel
                       ?? throw new InvalidOperationException("Station safety publisher is not started."))
                    .BasicPublishAsync(
                        _options.SafetyCommandExchange,
                        $"station.{request.StationId}.safe-stop",
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

            var acknowledgement = await completion.Task
                .WaitAsync(_options.SafetyAcknowledgementTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(acknowledgement.AgentId, request.AgentId, StringComparison.Ordinal)
                || !string.Equals(
                    acknowledgement.StationId,
                    request.StationId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Safe Stop acknowledgement Agent/Station identity does not match its request.");
            }

            return acknowledgement;
        }
        finally
        {
            _pending.TryRemove(request.MessageId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var waiter in _pending.Values)
        {
            waiter.TrySetException(new ObjectDisposedException(nameof(RabbitMqStationSafetyGateway)));
        }

        _pending.Clear();
        if (_acknowledgementChannel is not null)
        {
            await _acknowledgementChannel.DisposeAsync().ConfigureAwait(false);
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
        _startGate.Dispose();
    }

    private async ValueTask EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _started) == 1)
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _started) == 1)
            {
                return;
            }

            _connection = await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _publisherChannel = await _connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: true,
                        publisherConfirmationTrackingEnabled: true),
                    cancellationToken)
                .ConfigureAwait(false);
            _acknowledgementChannel = await _connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: false,
                        publisherConfirmationTrackingEnabled: false),
                    cancellationToken)
                .ConfigureAwait(false);
            await DeclareTopologyAsync(cancellationToken).ConfigureAwait(false);
            var consumer = new AsyncEventingBasicConsumer(_acknowledgementChannel);
            consumer.ReceivedAsync += HandleAcknowledgementAsync;
            await _acknowledgementChannel.BasicConsumeAsync(
                    AcknowledgementQueueName(),
                    autoAck: false,
                    consumerTag: string.Empty,
                    noLocal: false,
                    exclusive: false,
                    arguments: null,
                    consumer,
                    cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _started, 1);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task HandleAcknowledgementAsync(object sender, BasicDeliverEventArgs delivery)
    {
        _ = sender;
        var channel = _acknowledgementChannel
            ?? throw new InvalidOperationException("Station safety acknowledgement channel is not started.");
        try
        {
            if (!string.Equals(
                    delivery.BasicProperties.ContentType,
                    "application/json",
                    StringComparison.Ordinal)
                || !string.Equals(
                    delivery.BasicProperties.Type,
                    nameof(StationSafeStopAcknowledged),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid Station Safe Stop acknowledgement envelope.");
            }

            var acknowledgement = JsonSerializer.Deserialize<StationSafeStopAcknowledged>(
                delivery.Body.Span,
                JsonOptions)
                ?? throw new InvalidDataException("Station Safe Stop acknowledgement is null.");
            if (_pending.TryGetValue(acknowledgement.RequestMessageId, out var completion))
            {
                completion.TrySetResult(acknowledgement);
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
        catch
        {
            await channel.BasicNackAsync(
                    delivery.DeliveryTag,
                    multiple: false,
                    requeue: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask DeclareTopologyAsync(CancellationToken cancellationToken)
    {
        await _publisherChannel!.ExchangeDeclareAsync(
                _options.SafetyCommandExchange,
                ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _acknowledgementChannel!.ExchangeDeclareAsync(
                _options.SafetyEventExchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _acknowledgementChannel.QueueDeclareAsync(
                AcknowledgementQueueName(),
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _acknowledgementChannel.QueueBindAsync(
                AcknowledgementQueueName(),
                _options.SafetyEventExchange,
                "station.*.safe-stop-acknowledged",
                arguments: null,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await _acknowledgementChannel.BasicQosAsync(0, 8, global: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private string AcknowledgementQueueName() =>
        $"openlineops.coordinator.{_options.CoordinatorId}.station-safety-acks";
}
