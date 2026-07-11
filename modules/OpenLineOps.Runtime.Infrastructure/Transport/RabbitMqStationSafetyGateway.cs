using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class RabbitMqStationSafetyGateway :
    IStationEmergencyStopGateway,
    IStationSafetyGateway,
    IStationJobCancellationGateway,
    IAsyncDisposable
{
    private readonly StationCoordinatorTransportOptions _options;
    private readonly ConnectionFactory _emergencyFactory;
    private readonly ConnectionFactory _controlFactory;
    private readonly ConnectionFactory _acknowledgementFactory;
    private readonly StationSafetyAcknowledgementCorrelator _correlator = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _emergencyPublishGate = new(1, 1);
    private readonly SemaphoreSlim _controlPublishGate = new(1, 1);
    private IConnection? _emergencyConnection;
    private IConnection? _controlConnection;
    private IConnection? _acknowledgementConnection;
    private IChannel? _emergencyPublisherChannel;
    private IChannel? _controlPublisherChannel;
    private IChannel? _acknowledgementChannel;
    private int _started;
    private int _disposed;

    public RabbitMqStationSafetyGateway(StationCoordinatorTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SafetyAcknowledgementTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Station safety acknowledgement timeout must be positive.");
        }

        ValidateName(options.CoordinatorId, nameof(options.CoordinatorId));
        ValidateName(options.SafetyCommandExchange, nameof(options.SafetyCommandExchange));
        ValidateName(options.SafetyEventExchange, nameof(options.SafetyEventExchange));
        _options = options;
        _emergencyFactory = CreateFactory(
            options,
            $"OpenLineOps.Coordinator.Emergency/{options.CoordinatorId}");
        _controlFactory = CreateFactory(
            options,
            $"OpenLineOps.Coordinator.Control/{options.CoordinatorId}");
        _acknowledgementFactory = CreateFactory(
            options,
            $"OpenLineOps.Coordinator.SafetyAcks/{options.CoordinatorId}");
    }

    public async ValueTask<EmergencyStopAcknowledged> RequestEmergencyStopAsync(
        EmergencyStopRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        using var registration = _correlator.Register(request);
        await PublishAsync(
                StationCoordinatorPublicationFactory.Create(_options, request),
                _emergencyPublisherChannel
                ?? throw new InvalidOperationException("Emergency publisher is not started."),
                _emergencyPublishGate,
                cancellationToken)
            .ConfigureAwait(false);
        return await registration.Task
            .WaitAsync(_options.SafetyAcknowledgementTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<StationSafeStopAcknowledged> RequestSafeStopAsync(
        StationSafeStopRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        using var registration = _correlator.Register(request);
        await PublishAsync(
                StationCoordinatorPublicationFactory.Create(_options, request),
                _controlPublisherChannel
                ?? throw new InvalidOperationException("Station control publisher is not started."),
                _controlPublishGate,
                cancellationToken)
            .ConfigureAwait(false);
        return await registration.Task
            .WaitAsync(_options.SafetyAcknowledgementTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<StationJobCancelAcknowledged> RequestCancelAsync(
        StationJobCancelRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        using var registration = _correlator.Register(request);
        await PublishAsync(
                StationCoordinatorPublicationFactory.Create(_options, request),
                _controlPublisherChannel
                ?? throw new InvalidOperationException("Station control publisher is not started."),
                _controlPublishGate,
                cancellationToken)
            .ConfigureAwait(false);
        return await registration.Task
            .WaitAsync(_options.SafetyAcknowledgementTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _correlator.FailAll(
            new ObjectDisposedException(nameof(RabbitMqStationSafetyGateway)));
        await DisposeTransportAsync().ConfigureAwait(false);
        _controlPublishGate.Dispose();
        _emergencyPublishGate.Dispose();
        _startGate.Dispose();
    }

    private async ValueTask EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _started) == 1 && AllChannelsOpen())
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _started) == 1 && AllChannelsOpen())
            {
                return;
            }

            Volatile.Write(ref _started, 0);
            await DisposeTransportAsync().ConfigureAwait(false);
            _emergencyConnection = await _emergencyFactory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            _controlConnection = await _controlFactory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            _acknowledgementConnection = await _acknowledgementFactory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            _emergencyPublisherChannel = await CreatePublisherChannelAsync(
                    _emergencyConnection,
                    cancellationToken)
                .ConfigureAwait(false);
            _controlPublisherChannel = await CreatePublisherChannelAsync(
                    _controlConnection,
                    cancellationToken)
                .ConfigureAwait(false);
            _acknowledgementChannel = await _acknowledgementConnection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: false,
                        publisherConfirmationTrackingEnabled: false),
                    cancellationToken)
                .ConfigureAwait(false);

            await DeclarePublisherTopologyAsync(
                    _emergencyPublisherChannel,
                    cancellationToken)
                .ConfigureAwait(false);
            await DeclarePublisherTopologyAsync(
                    _controlPublisherChannel,
                    cancellationToken)
                .ConfigureAwait(false);
            await DeclareAcknowledgementTopologyAsync(
                    _acknowledgementChannel,
                    cancellationToken)
                .ConfigureAwait(false);

            var channel = _acknowledgementChannel;
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) =>
                HandleAcknowledgementAsync(channel, delivery).AsTask();
            await channel.BasicConsumeAsync(
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

    private async ValueTask HandleAcknowledgementAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery)
    {
        try
        {
            _correlator.Accept(ToDelivery(delivery));
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException
                                           or InvalidDataException
                                           or ArgumentException)
        {
            await channel.BasicRejectAsync(
                    delivery.DeliveryTag,
                    requeue: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }
        catch
        {
            await channel.BasicNackAsync(
                    delivery.DeliveryTag,
                    multiple: false,
                    requeue: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        await channel.BasicAckAsync(
                delivery.DeliveryTag,
                multiple: false,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async ValueTask PublishAsync(
        StationCoordinatorPublication publication,
        IChannel channel,
        SemaphoreSlim publishGate,
        CancellationToken cancellationToken)
    {
        await publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            if (publication.Priority is { } priority)
            {
                properties.Priority = priority;
            }
            // The channel has both publisher confirms and confirmation tracking enabled.
            // RabbitMQ.Client therefore throws PublishException for nack or mandatory return.
            await channel.BasicPublishAsync(
                    publication.Exchange,
                    publication.RoutingKey,
                    mandatory: true,
                    properties,
                    publication.Body,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            publishGate.Release();
        }
    }

    private async ValueTask DeclarePublisherTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
                _options.SafetyCommandExchange,
                ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask DeclareAcknowledgementTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
                _options.SafetyEventExchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.QueueDeclareAsync(
                AcknowledgementQueueName(),
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var route in new[]
                 {
                     "station.*.emergency-stop-acknowledged",
                     "station.*.safe-stop-acknowledged",
                     "station.*.job-cancel-acknowledged"
                 })
        {
            await channel.QueueBindAsync(
                    AcknowledgementQueueName(),
                    _options.SafetyEventExchange,
                    route,
                    arguments: null,
                    noWait: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await channel.BasicQosAsync(0, 16, global: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool AllChannelsOpen() => _emergencyPublisherChannel is { IsOpen: true }
        && _controlPublisherChannel is { IsOpen: true }
        && _acknowledgementChannel is { IsOpen: true };

    private async ValueTask DisposeTransportAsync()
    {
        await DisposeChannelAsync(_acknowledgementChannel).ConfigureAwait(false);
        await DisposeChannelAsync(_controlPublisherChannel).ConfigureAwait(false);
        await DisposeChannelAsync(_emergencyPublisherChannel).ConfigureAwait(false);
        await DisposeConnectionAsync(_acknowledgementConnection).ConfigureAwait(false);
        await DisposeConnectionAsync(_controlConnection).ConfigureAwait(false);
        await DisposeConnectionAsync(_emergencyConnection).ConfigureAwait(false);
        _acknowledgementChannel = null;
        _controlPublisherChannel = null;
        _emergencyPublisherChannel = null;
        _acknowledgementConnection = null;
        _controlConnection = null;
        _emergencyConnection = null;
        Volatile.Write(ref _started, 0);
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

    private static Task<IChannel> CreatePublisherChannelAsync(
        IConnection connection,
        CancellationToken cancellationToken) => connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);

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

    private string AcknowledgementQueueName() =>
        $"openlineops.coordinator.{_options.CoordinatorId}.station-safety-acks";

    private static async ValueTask DisposeChannelAsync(IChannel? channel)
    {
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeConnectionAsync(IConnection? connection)
    {
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateName(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new InvalidOperationException(
                $"Station transport {name} must be canonical text.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);
}
