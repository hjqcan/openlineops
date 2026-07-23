using System.Runtime.ExceptionServices;
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
            var shutdownRequested = cancellationToken.IsCancellationRequested;
            channelLifetime.Cancel();
            var sibling = ReferenceEquals(ended, emergency) ? control : emergency;

            var endedFailureTask = CaptureFailureAsync(ended);
            var siblingFailureTask = CaptureFailureAsync(sibling);
            await Task.WhenAll(endedFailureTask, siblingFailureTask).ConfigureAwait(false);
            var failures = new List<Exception>();
            var endedFailure = await endedFailureTask.ConfigureAwait(false);
            if (endedFailure is not null
                && !(shutdownRequested && endedFailure is OperationCanceledException))
            {
                failures.Add(endedFailure);
            }

            var siblingFailure = await siblingFailureTask.ConfigureAwait(false);
            if (siblingFailure is not null
                && siblingFailure is not OperationCanceledException)
            {
                failures.Add(siblingFailure);
            }

            if (endedFailure is null && !shutdownRequested)
            {
                failures.Add(new IOException(
                    "An independent Station safety channel ended unexpectedly."));
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(failures);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private int _disposed;

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

    public async Task RunAsync(
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> emergencyStopHandler,
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> safeStopHandler,
        Func<StationJobCancelRequested, CancellationToken, ValueTask<StationJobCancelExecutionResult>> jobCancelHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emergencyStopHandler);
        ArgumentNullException.ThrowIfNull(safeStopHandler);
        ArgumentNullException.ThrowIfNull(jobCancelHandler);
        ThrowIfDisposed();
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Station safety receiver is already running.");
        }

        try
        {
            ThrowIfDisposed();
            using var runLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            await _supervisor.RunAsync(
                    token => RunEmergencyChannelAsync(emergencyStopHandler, token),
                    token => RunControlChannelAsync(safeStopHandler, jobCancelHandler, token),
                    runLifetime.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
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

        var runStopped = false;
        try
        {
            runStopped = await _runGate
                .WaitAsync(RabbitMqTransportShutdown.MaximumTotalSessionShutdown)
                .ConfigureAwait(false);
            if (!runStopped)
            {
                failures.Add(new TimeoutException(
                    "Station safety receiver did not release its run gate within the bounded shutdown deadline."));
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            if (runStopped)
            {
                _runGate.Release();
            }
        }

        _lifetime.Dispose();
        ThrowCleanupFailures(failures);
    }

    private async Task RunEmergencyChannelAsync(
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> handler,
        CancellationToken cancellationToken)
    {
        IConnection? connection = null;
        IChannel? channel = null;
        string? consumerTag = null;
        var quiescence = new StationDeliveryQuiescence();
        using var stopRegistration = cancellationToken.UnsafeRegister(
            static state => ((StationDeliveryQuiescence)state!).StopAccepting(),
            quiescence);
        Exception? primaryFailure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection = await _emergencyFactory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            channel = await CreateConfirmedChannelAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await DeclareEmergencyTopologyAsync(channel, cancellationToken)
                .ConfigureAwait(false);

            var settlement = new RabbitMqSettlement(channel);
            var publisher = new RabbitMqAcknowledgementPublisher(channel);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => quiescence.ExecuteAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return _processor.ProcessEmergencyStopAsync(
                            ToDelivery(delivery),
                            handler,
                            publisher,
                            settlement,
                            cancellationToken)
                        .AsTask();
                },
                cancellationToken);
            consumerTag = await channel.BasicConsumeAsync(
                    EmergencyQueueName(),
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
            primaryFailure = exception;
        }

        quiescence.StopAccepting();
        var cleanupFailures = new List<Exception>();
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CancelConsumerAsync(channel, consumerTag));
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => new ValueTask(quiescence.StopAcceptingAndWaitAsync()));
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CloseChannelAsync(channel));
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CloseConnectionAsync(connection));
        ThrowSessionFailures(primaryFailure, cleanupFailures);
    }

    private async Task RunControlChannelAsync(
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> safeStopHandler,
        Func<StationJobCancelRequested, CancellationToken, ValueTask<StationJobCancelExecutionResult>> jobCancelHandler,
        CancellationToken cancellationToken)
    {
        IConnection? connection = null;
        IChannel? channel = null;
        string? safeStopConsumerTag = null;
        string? jobCancelConsumerTag = null;
        var quiescence = new StationDeliveryQuiescence();
        using var stopRegistration = cancellationToken.UnsafeRegister(
            static state => ((StationDeliveryQuiescence)state!).StopAccepting(),
            quiescence);
        Exception? primaryFailure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection = await _controlFactory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            channel = await CreateConfirmedChannelAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await DeclareControlTopologyAsync(channel, cancellationToken)
                .ConfigureAwait(false);

            var settlement = new RabbitMqSettlement(channel);
            var publisher = new RabbitMqAcknowledgementPublisher(channel);
            var safeStopConsumer = new AsyncEventingBasicConsumer(channel);
            safeStopConsumer.ReceivedAsync += (_, delivery) => quiescence.ExecuteAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return _processor.ProcessSafeStopAsync(
                            ToDelivery(delivery),
                            safeStopHandler,
                            publisher,
                            settlement,
                            cancellationToken)
                        .AsTask();
                },
                cancellationToken);
            safeStopConsumerTag = await channel.BasicConsumeAsync(
                    SafeStopQueueName(),
                    autoAck: false,
                    consumerTag: string.Empty,
                    noLocal: false,
                    exclusive: false,
                    arguments: null,
                    safeStopConsumer,
                    cancellationToken)
                .ConfigureAwait(false);

            var jobCancelConsumer = new AsyncEventingBasicConsumer(channel);
            jobCancelConsumer.ReceivedAsync += (_, delivery) => quiescence.ExecuteAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return _processor.ProcessJobCancelAsync(
                            ToDelivery(delivery),
                            jobCancelHandler,
                            publisher,
                            settlement,
                            cancellationToken)
                        .AsTask();
                },
                cancellationToken);
            jobCancelConsumerTag = await channel.BasicConsumeAsync(
                    JobCancelQueueName(),
                    autoAck: false,
                    consumerTag: string.Empty,
                    noLocal: false,
                    exclusive: false,
                    arguments: null,
                    jobCancelConsumer,
                    cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        quiescence.StopAccepting();
        var cleanupFailures = new List<Exception>();
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CancelConsumerAsync(channel, safeStopConsumerTag));
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CancelConsumerAsync(channel, jobCancelConsumerTag));
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => new ValueTask(quiescence.StopAcceptingAndWaitAsync()));
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CloseChannelAsync(channel));
        await CaptureCleanupFailuresAsync(
            cleanupFailures,
            () => RabbitMqTransportShutdown.CloseConnectionAsync(connection));
        ThrowSessionFailures(primaryFailure, cleanupFailures);
    }

    private async ValueTask DeclareEmergencyTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await DeclareExchangesAsync(channel, cancellationToken).ConfigureAwait(false);
        await DeclarePriorityQueueAsync(
                channel,
                EmergencyQueueName(),
                StationTransportRoute.Safety(
                    _options.AgentId,
                    _options.StationId,
                    "emergency-stop"),
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
                StationTransportRoute.Safety(
                    _options.AgentId,
                    _options.StationId,
                    "safe-stop"),
                cancellationToken)
            .ConfigureAwait(false);
        await DeclarePriorityQueueAsync(
                channel,
                JobCancelQueueName(),
                StationTransportRoute.Safety(
                    _options.AgentId,
                    _options.StationId,
                    "job-cancel"),
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
        StationTransportRoute.SafetyQueue(
            _options.AgentId,
            _options.StationId,
            "emergency-stop");

    private string SafeStopQueueName() =>
        StationTransportRoute.SafetyQueue(
            _options.AgentId,
            _options.StationId,
            "safe-stop");

    private string JobCancelQueueName() =>
        StationTransportRoute.SafetyQueue(
            _options.AgentId,
            _options.StationId,
            "job-cancel");

    private static async ValueTask CaptureCleanupFailuresAsync(
        List<Exception> failures,
        params Func<ValueTask>[] cleanupActions)
    {
        var results = await Task.WhenAll(cleanupActions.Select(CaptureAsync))
            .ConfigureAwait(false);
        failures.AddRange(results.OfType<Exception>());
    }

    private static async Task<Exception?> CaptureAsync(Func<ValueTask> cleanupAction)
    {
        try
        {
            await cleanupAction().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void ThrowSessionFailures(
        Exception? primaryFailure,
        List<Exception> cleanupFailures)
    {
        if (primaryFailure is null)
        {
            ThrowCleanupFailures(cleanupFailures);
            return;
        }

        if (cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            return;
        }

        throw new AggregateException(
            new[] { primaryFailure }.Concat(cleanupFailures));
    }

    private static void ThrowCleanupFailures(List<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
            return;
        }

        throw new AggregateException(failures);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

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
