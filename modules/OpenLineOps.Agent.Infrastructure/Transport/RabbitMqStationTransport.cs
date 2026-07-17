using System.Runtime.ExceptionServices;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed record RabbitMqStationTransportOptions(
    Uri BrokerUri,
    string AgentId,
    string StationId,
    string StationSystemId,
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
    private readonly ConnectionFactory _publisherFactory;
    private readonly ConnectionFactory _receiverFactory;
    private readonly IStationAgentConfirmedPublicationTransport? _publicationTransport;
    private readonly StationJobDeliveryProcessor _deliveryProcessor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly SemaphoreSlim _receiverRunGate = new(1, 1);
    private IConnection? _publisherConnection;
    private IChannel? _publisherChannel;
    private int _disposed;

    public RabbitMqStationTransport(
        RabbitMqStationTransportOptions options,
        IStationAgentConfirmedPublicationTransport? publicationTransport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BrokerUri);
        _ = Required(options.AgentId, nameof(options.AgentId));
        _ = Required(options.StationId, nameof(options.StationId));
        _ = Required(options.StationSystemId, nameof(options.StationSystemId));
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
        _publisherFactory = CreateFactory(
            options,
            $"OpenLineOps.Agent.Publisher/{options.AgentId}/{options.StationId}",
            consumerDispatchConcurrency: 1);
        _receiverFactory = CreateFactory(
            options,
            $"OpenLineOps.Agent.Receiver/{options.AgentId}/{options.StationId}",
            options.MaximumConcurrentJobs);
    }

    public async ValueTask PublishAsync(
        string kind,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = operation.Token;
        var publication = StationAgentEventPublicationFactory.Create(_options, kind, payloadJson);
        await _publishGate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_publicationTransport is not null)
            {
                await _publicationTransport.PublishAsync(publication, operationToken)
                    .ConfigureAwait(false);
                return;
            }

            var channel = await GetPublisherChannelAsync(operationToken).ConfigureAwait(false);
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
                        operationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                try
                {
                    await InvalidatePublisherChannelAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(exception, cleanupException);
                }

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
        ThrowIfDisposed();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = operation.Token;
        await _receiverRunGate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await RunReceiverSessionAsync(
                    handler,
                    resourceLeaseHandler,
                    operationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _receiverRunGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var failures = new List<Exception>();
        try
        {
            _lifetime.Cancel();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        var publishGateAcquired = await TryAcquireGateAsync(
                _publishGate,
                TimeSpan.FromSeconds(5),
                "RabbitMQ publisher operations did not stop before disposal.",
                failures)
            .ConfigureAwait(false);
        var publisherChannel = Interlocked.Exchange(ref _publisherChannel, null);
        var publisherConnection = Interlocked.Exchange(ref _publisherConnection, null);
        if (publishGateAcquired)
        {
            _publishGate.Release();
        }

        var receiverGateAcquired = await TryAcquireGateAsync(
                _receiverRunGate,
                RabbitMqTransportShutdown.MaximumTotalSessionShutdown,
                "RabbitMQ receiver session did not stop before disposal.",
                failures)
            .ConfigureAwait(false);
        if (receiverGateAcquired)
        {
            _receiverRunGate.Release();
        }

        await CaptureCloseFailuresAsync(
            failures,
            () => RabbitMqTransportShutdown.CloseChannelAsync(publisherChannel));
        await CaptureCloseFailuresAsync(
            failures,
            () => RabbitMqTransportShutdown.CloseConnectionAsync(publisherConnection));
        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    private async Task RunReceiverSessionAsync(
        Func<StationJobRequested, CancellationToken, ValueTask> handler,
        Func<ResourceLeaseChanged, CancellationToken, ValueTask> resourceLeaseHandler,
        CancellationToken cancellationToken)
    {
        IConnection? connection = null;
        IChannel? channel = null;
        string? consumerTag = null;
        Exception? operationFailure = null;
        var quiescence = new StationDeliveryQuiescence();
        using var stopping = cancellationToken.UnsafeRegister(
            static state => ((StationDeliveryQuiescence)state!).StopAccepting(),
            quiescence);
        try
        {
            connection = await _receiverFactory.CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            channel = await CreateReceiverChannelAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            var settlement = new RabbitMqSettlement(channel);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => quiescence.ExecuteAsync(
                () => _deliveryProcessor.ProcessAsync(
                        ToDelivery(delivery),
                        handler,
                        resourceLeaseHandler,
                        settlement,
                        cancellationToken)
                    .AsTask(),
                cancellationToken);
            consumerTag = await channel.BasicConsumeAsync(
                    QueueName(),
                    autoAck: false,
                    consumerTag: string.Empty,
                    noLocal: false,
                    exclusive: false,
                    arguments: null,
                    consumer,
                    cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        quiescence.StopAccepting();
        var cleanupFailures = new List<Exception>();
        await CaptureCloseFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CancelConsumerAsync(channel, consumerTag));
        await CaptureTaskFailuresAsync(
            cleanupFailures,
            () => quiescence.StopAcceptingAndWaitAsync());
        await CaptureCloseFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CloseChannelAsync(channel));
        await CaptureCloseFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CloseConnectionAsync(connection));
        ThrowOperationFailure(operationFailure, cleanupFailures);
    }

    private async ValueTask<IChannel> GetPublisherChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await GetPublisherConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (_publisherChannel is { IsOpen: true })
        {
            return _publisherChannel;
        }

        var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken)
            .ConfigureAwait(false);
        var ownsChannel = true;
        try
        {
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
            if (Volatile.Read(ref _disposed) != 0)
            {
                ObjectDisposedException.ThrowIf(true, this);
            }

            _publisherChannel = channel;
            ownsChannel = false;
            if (Volatile.Read(ref _disposed) == 0)
            {
                return channel;
            }

            ownsChannel = ReferenceEquals(
                Interlocked.CompareExchange(ref _publisherChannel, null, channel),
                channel);
            ObjectDisposedException.ThrowIf(true, this);
            return channel;
        }
        catch (Exception exception)
        {
            var cleanupFailures = new List<Exception>();
            if (ownsChannel)
            {
                await CaptureCloseFailuresAsync(
                    cleanupFailures,
                    () => RabbitMqTransportShutdown.CloseChannelAsync(channel));
            }

            ThrowOperationFailure(exception, cleanupFailures);
            throw;
        }
    }

    private async ValueTask InvalidatePublisherChannelAsync()
    {
        var channel = Interlocked.Exchange(ref _publisherChannel, null);
        if (channel is not null)
        {
            await RabbitMqTransportShutdown.CloseChannelAsync(channel)
                .ConfigureAwait(false);
        }
    }

    private async Task<IChannel> CreateReceiverChannelAsync(
        IConnection connection,
        CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false),
                cancellationToken)
            .ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(
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
        await channel.QueueDeclareAsync(
                queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.QueueBindAsync(
                queueName,
                _options.JobExchange,
                StationTransportRoute.Job(_options.AgentId, _options.StationId),
                arguments: null,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.QueueBindAsync(
                queueName,
                _options.JobExchange,
                StationTransportRoute.ResourceLeaseChanged(
                    _options.AgentId,
                    _options.StationId),
                arguments: null,
                noWait: false,
                cancellationToken)
            .ConfigureAwait(false);
        await channel.BasicQosAsync(
                prefetchSize: 0,
                _options.PrefetchCount,
                global: false,
                cancellationToken)
            .ConfigureAwait(false);
        return channel;
    }

    private async ValueTask<IConnection> GetPublisherConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (_publisherConnection is { IsOpen: true } connection)
        {
            return connection;
        }

        var staleChannel = Interlocked.Exchange(ref _publisherChannel, null);
        var staleConnection = Interlocked.Exchange(ref _publisherConnection, null);
        var staleCleanupFailures = new List<Exception>();
        await CaptureCloseFailuresAsync(
            staleCleanupFailures,
            () => RabbitMqTransportShutdown.CloseChannelAsync(staleChannel));
        await CaptureCloseFailuresAsync(
            staleCleanupFailures,
            () => RabbitMqTransportShutdown.CloseConnectionAsync(staleConnection));
        ThrowOperationFailure(
            operationFailure: null,
            cleanupFailures: staleCleanupFailures);
        var created = await _publisherFactory.CreateConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0)
        {
            var disposedFailure = new ObjectDisposedException(
                nameof(RabbitMqStationTransport));
            var cleanupFailures = new List<Exception>();
            await CaptureCloseFailuresAsync(
                cleanupFailures,
                () => RabbitMqTransportShutdown.CloseConnectionAsync(created));
            ThrowOperationFailure(disposedFailure, cleanupFailures);
        }

        _publisherConnection = created;
        if (Volatile.Read(ref _disposed) == 0)
        {
            return created;
        }

        var ownsCreatedConnection = ReferenceEquals(
                Interlocked.CompareExchange(ref _publisherConnection, null, created),
                created);
        var disposalCleanupFailures = new List<Exception>();
        if (ownsCreatedConnection)
        {
            await CaptureCloseFailuresAsync(
                disposalCleanupFailures,
                () => RabbitMqTransportShutdown.CloseConnectionAsync(created));
        }

        ThrowOperationFailure(
            new ObjectDisposedException(nameof(RabbitMqStationTransport)),
            disposalCleanupFailures);
        return created;
    }

    private static ConnectionFactory CreateFactory(
        RabbitMqStationTransportOptions options,
        string clientProvidedName,
        ushort consumerDispatchConcurrency) => new()
        {
            Uri = options.BrokerUri,
            ClientProvidedName = clientProvidedName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = consumerDispatchConcurrency
        };

    private string QueueName() =>
        StationTransportRoute.JobQueue(_options.AgentId, _options.StationId);

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

    private static async ValueTask CaptureCloseFailuresAsync(
        List<Exception> failures,
        params Func<ValueTask>[] closes)
    {
        var results = await Task.WhenAll(closes.Select(CaptureAsync))
            .ConfigureAwait(false);
        failures.AddRange(results.OfType<Exception>());

        static async Task<Exception?> CaptureAsync(Func<ValueTask> close)
        {
            try
            {
                await close().ConfigureAwait(false);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }

    private static async ValueTask CaptureTaskFailuresAsync(
        List<Exception> failures,
        params Func<Task>[] tasks)
    {
        var results = await Task.WhenAll(tasks.Select(CaptureAsync))
            .ConfigureAwait(false);
        failures.AddRange(results.OfType<Exception>());

        static async Task<Exception?> CaptureAsync(Func<Task> task)
        {
            try
            {
                await task().ConfigureAwait(false);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }

    private static async ValueTask<bool> TryAcquireGateAsync(
        SemaphoreSlim gate,
        TimeSpan timeout,
        string failureMessage,
        List<Exception> failures)
    {
        if (await gate.WaitAsync(timeout).ConfigureAwait(false))
        {
            return true;
        }

        failures.Add(new TimeoutException(failureMessage));
        return false;
    }

    private static void ThrowOperationFailure(
        Exception? operationFailure,
        List<Exception> cleanupFailures)
    {
        if (operationFailure is null)
        {
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(cleanupFailures);
            }

            return;
        }

        if (cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        throw new AggregateException(cleanupFailures.Prepend(operationFailure));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}
