using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed record RabbitMqStationSafetyOptions(
    Uri BrokerUri,
    string AgentId,
    string StationId,
    string CommandExchange = "openlineops.station.safety",
    string EventExchange = "openlineops.station.safety-events",
    bool RequireTls = true);

public sealed class RabbitMqStationSafetyReceiver : IStationSafetyReceiver, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly RabbitMqStationSafetyOptions _options;
    private readonly StationSafetyCommandCoordinator _coordinator;
    private readonly ConnectionFactory _factory;
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqStationSafetyReceiver(
        RabbitMqStationSafetyOptions options,
        StationSafetyCommandCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BrokerUri);
        ArgumentNullException.ThrowIfNull(coordinator);
        _ = Required(options.AgentId, nameof(options.AgentId));
        _ = Required(options.StationId, nameof(options.StationId));
        _ = Required(options.CommandExchange, nameof(options.CommandExchange));
        _ = Required(options.EventExchange, nameof(options.EventExchange));
        if (options.RequireTls
            && !string.Equals(options.BrokerUri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Station safety channel requires an amqps URI.",
                nameof(options));
        }

        _options = options;
        _coordinator = coordinator;
        _factory = new ConnectionFactory
        {
            Uri = options.BrokerUri,
            ClientProvidedName = $"OpenLineOps.Agent.Safety/{options.AgentId}/{options.StationId}",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };
    }

    public async Task RunAsync(
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> emergencyStopHandler,
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> safeStopHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emergencyStopHandler);
        ArgumentNullException.ThrowIfNull(safeStopHandler);
        _connection = await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken)
            .ConfigureAwait(false);
        await DeclareTopologyAsync(_channel, cancellationToken).ConfigureAwait(false);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                if (!string.Equals(
                        delivery.BasicProperties.ContentType,
                        "application/json",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Emergency stop message content type must be application/json.");
                }

                var request = JsonSerializer.Deserialize<EmergencyStopRequested>(
                    delivery.Body.Span,
                    JsonOptions)
                    ?? throw new InvalidDataException("Emergency stop request is null.");
                Validate(request);
                var acknowledgement = await _coordinator
                    .HandleEmergencyStopAsync(request, emergencyStopHandler, cancellationToken)
                    .ConfigureAwait(false);
                await PublishAcknowledgementAsync(_channel, acknowledgement, cancellationToken)
                    .ConfigureAwait(false);
                await _channel.BasicAckAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException
                                               or InvalidDataException
                                               or ArgumentException)
            {
                await _channel.BasicRejectAsync(
                        delivery.DeliveryTag,
                        requeue: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _channel.BasicNackAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        requeue: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                await _channel.BasicNackAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        requeue: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        };
        await _channel.BasicConsumeAsync(
                QueueName(),
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer,
                cancellationToken)
            .ConfigureAwait(false);
        var safeStopConsumer = new AsyncEventingBasicConsumer(_channel);
        safeStopConsumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                if (!string.Equals(
                        delivery.BasicProperties.ContentType,
                        "application/json",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Safe stop message content type must be application/json.");
                }

                var request = JsonSerializer.Deserialize<StationSafeStopRequested>(
                    delivery.Body.Span,
                    JsonOptions)
                    ?? throw new InvalidDataException("Safe stop request is null.");
                Validate(request);
                var acknowledgement = await _coordinator
                    .HandleSafeStopAsync(request, safeStopHandler, cancellationToken)
                    .ConfigureAwait(false);
                await PublishAcknowledgementAsync(_channel, acknowledgement, cancellationToken)
                    .ConfigureAwait(false);
                await _channel.BasicAckAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException
                                               or InvalidDataException
                                               or ArgumentException)
            {
                await _channel.BasicRejectAsync(
                        delivery.DeliveryTag,
                        requeue: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _channel.BasicNackAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        requeue: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                await _channel.BasicNackAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        requeue: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        };
        await _channel.BasicConsumeAsync(
                SafeStopQueueName(),
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                safeStopConsumer,
                cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask DeclareTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
                _options.CommandExchange,
                ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(
                _options.EventExchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        var queueArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-max-priority"] = (byte)10
        };
        await channel.QueueDeclareAsync(
                QueueName(),
                durable: true,
                exclusive: false,
                autoDelete: false,
                queueArguments,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.QueueBindAsync(
                QueueName(),
                _options.CommandExchange,
                $"station.{_options.StationId}.emergency-stop",
                arguments: null,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.QueueDeclareAsync(
                SafeStopQueueName(),
                durable: true,
                exclusive: false,
                autoDelete: false,
                queueArguments,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.QueueBindAsync(
                SafeStopQueueName(),
                _options.CommandExchange,
                $"station.{_options.StationId}.safe-stop",
                arguments: null,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.BasicQosAsync(0, 1, global: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishAcknowledgementAsync(
        IChannel channel,
        EmergencyStopAcknowledged response,
        CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            Type = nameof(EmergencyStopAcknowledged),
            AppId = _options.AgentId,
            MessageId = response.MessageId.ToString("D"),
            CorrelationId = response.RequestMessageId.ToString("D")
        };
        await channel.BasicPublishAsync(
                _options.EventExchange,
                $"station.{_options.StationId}.emergency-stop-acknowledged",
                mandatory: true,
                properties,
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask PublishAcknowledgementAsync(
        IChannel channel,
        StationSafeStopAcknowledged response,
        CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            Type = nameof(StationSafeStopAcknowledged),
            AppId = _options.AgentId,
            MessageId = response.MessageId.ToString("D"),
            CorrelationId = response.RequestMessageId.ToString("D")
        };
        await channel.BasicPublishAsync(
                _options.EventExchange,
                $"station.{_options.StationId}.safe-stop-acknowledged",
                mandatory: true,
                properties,
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void Validate(EmergencyStopRequested request)
    {
        if (request.MessageId == Guid.Empty
            || !string.Equals(request.AgentId, _options.AgentId, StringComparison.Ordinal)
            || !string.Equals(request.StationId, _options.StationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Emergency stop request identity does not target this Agent and Station.");
        }

        _ = Required(request.IdempotencyKey, nameof(request.IdempotencyKey));
        _ = Required(request.Reason, nameof(request.Reason));
        _ = Required(request.RequestedBy, nameof(request.RequestedBy));
        if (request.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Emergency stop timestamp must use UTC offset zero.");
        }
    }

    private void Validate(StationSafeStopRequested request)
    {
        if (request.MessageId == Guid.Empty
            || request.ProductionRunId == Guid.Empty
            || !string.Equals(request.AgentId, _options.AgentId, StringComparison.Ordinal)
            || !string.Equals(request.StationId, _options.StationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Safe stop request identity does not target this Agent and Station.");
        }

        _ = Required(request.IdempotencyKey, nameof(request.IdempotencyKey));
        _ = Required(request.StationSystemId, nameof(request.StationSystemId));
        _ = Required(request.ActorId, nameof(request.ActorId));
        _ = Required(request.Reason, nameof(request.Reason));
        if (request.OperationRunId is not null)
        {
            _ = Required(request.OperationRunId, nameof(request.OperationRunId));
        }

        if (request.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Safe stop timestamp must use UTC offset zero.");
        }
    }

    private string QueueName() => $"openlineops.station.{_options.StationId}.emergency-stop";

    private string SafeStopQueueName() => $"openlineops.station.{_options.StationId}.safe-stop";

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException($"{parameterName} must be canonical non-empty text.", parameterName)
            : value;
}
