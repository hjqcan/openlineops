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

public sealed class StationSafetyChannelSupervisor
{
    private int _running;

    public async Task RunAsync(
        Func<CancellationToken, Task> emergencyChannel,
        Func<CancellationToken, Task> controlChannel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emergencyChannel);
        ArgumentNullException.ThrowIfNull(controlChannel);
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("Station safety channel supervisor is already running.");
        }

        try
        {
            using var channelLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var emergency = emergencyChannel(channelLifetime.Token);
            var control = controlChannel(channelLifetime.Token);
            var ended = await Task.WhenAny(emergency, control).ConfigureAwait(false);
            channelLifetime.Cancel();
            var sibling = ReferenceEquals(ended, emergency) ? control : emergency;
            try
            {
                await sibling.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (channelLifetime.IsCancellationRequested)
            {
            }

            await ended.ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException(
                    "An independent Station safety channel ended unexpectedly.");
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}

public sealed class RabbitMqStationSafetyReceiver : IStationSafetyReceiver, IAsyncDisposable
{
    private readonly RabbitMqStationSafetyOptions _options;
    private readonly StationSafetyDeliveryProcessor _processor;
    private readonly ConnectionFactory _emergencyFactory;
    private readonly ConnectionFactory _controlFactory;
    private readonly StationSafetyChannelSupervisor _supervisor;
    private IConnection? _emergencyConnection;
    private IConnection? _controlConnection;
    private IChannel? _emergencyChannel;
    private IChannel? _controlChannel;

    public RabbitMqStationSafetyReceiver(
        RabbitMqStationSafetyOptions options,
        StationSafetyCommandCoordinator coordinator,
        StationSafetyChannelSupervisor? supervisor = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BrokerUri);
        ArgumentNullException.ThrowIfNull(coordinator);
        _ = Required(options.AgentId, nameof(options.AgentId));
        _ = Required(options.StationId, nameof(options.StationId));
        _ = Required(options.CommandExchange, nameof(options.CommandExchange));
        _ = Required(options.EventExchange, nameof(options.EventExchange));
        if (options.BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new ArgumentException(
                "Station safety BrokerUri must use amqp or amqps.",
                nameof(options));
        }

        if (options.RequireTls
            && !string.Equals(options.BrokerUri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Station safety channel requires an amqps URI.",
                nameof(options));
        }

        _options = options;
        _processor = new StationSafetyDeliveryProcessor(options, coordinator);
        _supervisor = supervisor ?? new StationSafetyChannelSupervisor();
        _emergencyFactory = CreateFactory(
            options,
            $"OpenLineOps.Agent.Emergency/{options.AgentId}/{options.StationId}");
        _controlFactory = CreateFactory(
            options,
            $"OpenLineOps.Agent.Control/{options.AgentId}/{options.StationId}");
    }

    public Task RunAsync(
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> emergencyStopHandler,
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> safeStopHandler,
        Func<StationJobCancelRequested, CancellationToken, ValueTask<StationJobCancelExecutionResult>> jobCancelHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emergencyStopHandler);
        ArgumentNullException.ThrowIfNull(safeStopHandler);
        ArgumentNullException.ThrowIfNull(jobCancelHandler);
        return _supervisor.RunAsync(
            token => RunEmergencyChannelAsync(emergencyStopHandler, token),
            token => RunControlChannelAsync(safeStopHandler, jobCancelHandler, token),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeChannelAsync(_emergencyChannel).ConfigureAwait(false);
        await DisposeChannelAsync(_controlChannel).ConfigureAwait(false);
        await DisposeConnectionAsync(_emergencyConnection).ConfigureAwait(false);
        await DisposeConnectionAsync(_controlConnection).ConfigureAwait(false);
    }

    private async Task RunEmergencyChannelAsync(
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> handler,
        CancellationToken cancellationToken)
    {
        _emergencyConnection = await _emergencyFactory
            .CreateConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        _emergencyChannel = await CreateConfirmedChannelAsync(
                _emergencyConnection,
                cancellationToken)
            .ConfigureAwait(false);
        await DeclareEmergencyTopologyAsync(_emergencyChannel, cancellationToken)
            .ConfigureAwait(false);

        var settlement = new RabbitMqSettlement(_emergencyChannel);
        var publisher = new RabbitMqAcknowledgementPublisher(_emergencyChannel);
        var consumer = new AsyncEventingBasicConsumer(_emergencyChannel);
        consumer.ReceivedAsync += (_, delivery) => _processor.ProcessEmergencyStopAsync(
                ToDelivery(delivery),
                handler,
                publisher,
                settlement,
                cancellationToken)
            .AsTask();
        await _emergencyChannel.BasicConsumeAsync(
                EmergencyQueueName(),
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

    private async Task RunControlChannelAsync(
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> safeStopHandler,
        Func<StationJobCancelRequested, CancellationToken, ValueTask<StationJobCancelExecutionResult>> jobCancelHandler,
        CancellationToken cancellationToken)
    {
        _controlConnection = await _controlFactory
            .CreateConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        _controlChannel = await CreateConfirmedChannelAsync(_controlConnection, cancellationToken)
            .ConfigureAwait(false);
        await DeclareControlTopologyAsync(_controlChannel, cancellationToken)
            .ConfigureAwait(false);

        var settlement = new RabbitMqSettlement(_controlChannel);
        var publisher = new RabbitMqAcknowledgementPublisher(_controlChannel);
        var safeStopConsumer = new AsyncEventingBasicConsumer(_controlChannel);
        safeStopConsumer.ReceivedAsync += (_, delivery) => _processor.ProcessSafeStopAsync(
                ToDelivery(delivery),
                safeStopHandler,
                publisher,
                settlement,
                cancellationToken)
            .AsTask();
        await _controlChannel.BasicConsumeAsync(
                SafeStopQueueName(),
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                safeStopConsumer,
                cancellationToken)
            .ConfigureAwait(false);

        var jobCancelConsumer = new AsyncEventingBasicConsumer(_controlChannel);
        jobCancelConsumer.ReceivedAsync += (_, delivery) => _processor.ProcessJobCancelAsync(
                ToDelivery(delivery),
                jobCancelHandler,
                publisher,
                settlement,
                cancellationToken)
            .AsTask();
        await _controlChannel.BasicConsumeAsync(
                JobCancelQueueName(),
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                jobCancelConsumer,
                cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DeclareEmergencyTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await DeclareExchangesAsync(channel, cancellationToken).ConfigureAwait(false);
        await DeclarePriorityQueueAsync(
                channel,
                EmergencyQueueName(),
                $"station.{_options.StationId}.emergency-stop",
                cancellationToken)
            .ConfigureAwait(false);
        await channel.BasicQosAsync(0, 1, global: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask DeclareControlTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await DeclareExchangesAsync(channel, cancellationToken).ConfigureAwait(false);
        await DeclarePriorityQueueAsync(
                channel,
                SafeStopQueueName(),
                $"station.{_options.StationId}.safe-stop",
                cancellationToken)
            .ConfigureAwait(false);
        await DeclarePriorityQueueAsync(
                channel,
                JobCancelQueueName(),
                $"station.{_options.StationId}.job-cancel",
                cancellationToken)
            .ConfigureAwait(false);
        await channel.BasicQosAsync(0, 1, global: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask DeclareExchangesAsync(
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
    }

    private async ValueTask DeclarePriorityQueueAsync(
        IChannel channel,
        string queueName,
        string routingKey,
        CancellationToken cancellationToken)
    {
        var queueArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-max-priority"] = (byte)10
        };
        await channel.QueueDeclareAsync(
                queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                queueArguments,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.QueueBindAsync(
                queueName,
                _options.CommandExchange,
                routingKey,
                arguments: null,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ConnectionFactory CreateFactory(
        RabbitMqStationSafetyOptions options,
        string clientProvidedName) => new()
        {
            Uri = options.BrokerUri,
            ClientProvidedName = clientProvidedName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };

    private static Task<IChannel> CreateConfirmedChannelAsync(
        IConnection connection,
        CancellationToken cancellationToken) => connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);

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

    private string EmergencyQueueName() =>
        $"openlineops.station.{_options.StationId}.emergency-stop";

    private string SafeStopQueueName() =>
        $"openlineops.station.{_options.StationId}.safe-stop";

    private string JobCancelQueueName() =>
        $"openlineops.station.{_options.StationId}.job-cancel";

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

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;

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

    private sealed class RabbitMqAcknowledgementPublisher(IChannel channel)
        : IStationSafetyAcknowledgementPublisher
    {
        public ValueTask PublishAsync(
            StationAgentEventPublication publication,
            CancellationToken cancellationToken = default)
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
            return channel.BasicPublishAsync(
                publication.Exchange,
                publication.RoutingKey,
                mandatory: true,
                properties,
                publication.Body,
                cancellationToken);
        }
    }
}
