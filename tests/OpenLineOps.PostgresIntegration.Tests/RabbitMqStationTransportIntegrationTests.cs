using System.Collections.Concurrent;
using System.Text.Json;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Transport;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(RabbitMqContainerGroup.Name)]
public sealed class RabbitMqStationTransportIntegrationTests
{
    private readonly RabbitMqContainerFixture _rabbitMq;

    public RabbitMqStationTransportIntegrationTests(RabbitMqContainerFixture rabbitMq)
    {
        _rabbitMq = rabbitMq;
    }

    [RabbitMqIntegrationFact]
    public async Task JobPublishRedeliveryResultInboxAndMandatoryReturnAreBrokerConfirmed()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station-{suffix}";
        var agentId = $"agent-{suffix}";
        var coordinatorId = $"coordinator-{suffix}";
        var jobExchange = $"openlineops.integration.jobs.{suffix}";
        var eventExchange = $"openlineops.integration.events.{suffix}";
        var brokerUri = BrokerUri();
        var store = new InMemoryStationJobCoordinationStore();
        var coordinatorOptions = new StationCoordinatorTransportOptions
        {
            BrokerUri = brokerUri.AbsoluteUri,
            RequireTls = false,
            CoordinatorId = coordinatorId,
            JobExchange = jobExchange,
            EventExchange = eventExchange
        };
        await using var coordinator = new RabbitMqStationCoordinatorTransport(
            coordinatorOptions,
            store);
        await using var agent = new RabbitMqStationTransport(
            new RabbitMqStationTransportOptions(
                brokerUri,
                agentId,
                stationId,
                jobExchange,
                eventExchange,
                PrefetchCount: 1,
                MaximumConcurrentJobs: 1,
                RequireTls: false));
        await DeclareJobTopologyAsync(
            brokerUri,
            coordinatorId,
            stationId,
            jobExchange,
            eventExchange);

        using var agentStop = new CancellationTokenSource();
        var calls = 0;
        var physicalExecutions = 0;
        var durablyAccepted = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var redelivered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedAfterReturn = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var agentLoop = agent.RunAsync(
            (request, _) =>
            {
                if (durablyAccepted.TryAdd(request.IdempotencyKey, 0))
                {
                    Interlocked.Increment(ref physicalExecutions);
                }

                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new IOException("Injected disconnect after durable Inbox checkpoint.");
                }

                if (Volatile.Read(ref calls) == 2)
                {
                    redelivered.TrySetResult();
                }
                else
                {
                    resumedAfterReturn.TrySetResult();
                }

                return ValueTask.CompletedTask;
            },
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            agentStop.Token);

        var request = JobRequest(agentId, stationId);
        await coordinator.PublishAsync(request);
        await redelivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, Volatile.Read(ref physicalExecutions));

        using var resultStop = new CancellationTokenSource();
        var resultLoop = coordinator.RunResultInboxAsync(
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            resultStop.Token);
        var completion = Completion(request);
        await agent.PublishAsync(
            nameof(StationJobCompleted),
            JsonSerializer.Serialize(completion, JsonOptions()));
        await WaitUntilAsync(
            async () => await store.GetCompletionAsync(request.IdempotencyKey) is not null,
            TimeSpan.FromSeconds(10));

        var unroutable = JobRequest(agentId, $"missing-{suffix}");
        await Assert.ThrowsAsync<PublishException>(async () =>
            await coordinator.PublishAsync(unroutable));
        await coordinator.PublishAsync(JobRequest(agentId, stationId));
        await resumedAfterReturn.Task.WaitAsync(TimeSpan.FromSeconds(10));

        resultStop.Cancel();
        agentStop.Cancel();
        await ObserveCancellationAsync(resultLoop);
        await ObserveCancellationAsync(agentLoop);
    }

    [RabbitMqIntegrationFact]
    public async Task EmergencyStopAcknowledgesWhileSafeStopControlHandlerIsBlocked()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station-{suffix}";
        var agentId = $"agent-{suffix}";
        var coordinatorId = $"coordinator-{suffix}";
        var commandExchange = $"openlineops.integration.safety.{suffix}";
        var eventExchange = $"openlineops.integration.safety-events.{suffix}";
        var brokerUri = BrokerUri();
        await DeclareSafetyCommandTopologyAsync(
            brokerUri,
            stationId,
            commandExchange,
            eventExchange);

        var safetyCoordinator = new StationSafetyCommandCoordinator(
            new ThreadSafeSafetyInboxStore(),
            new SystemUtcClock());
        await using var receiver = new RabbitMqStationSafetyReceiver(
            new RabbitMqStationSafetyOptions(
                brokerUri,
                agentId,
                stationId,
                commandExchange,
                eventExchange,
                RequireTls: false),
            safetyCoordinator);
        var gatewayOptions = new StationCoordinatorTransportOptions
        {
            BrokerUri = brokerUri.AbsoluteUri,
            RequireTls = false,
            CoordinatorId = coordinatorId,
            SafetyCommandExchange = commandExchange,
            SafetyEventExchange = eventExchange,
            SafetyAcknowledgementTimeout = TimeSpan.FromSeconds(10)
        };
        await using var gateway = new RabbitMqStationSafetyGateway(gatewayOptions);
        using var receiverStop = new CancellationTokenSource();
        var safeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSafe = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverLoop = receiver.RunAsync(
            (_, _) => ValueTask.FromResult(new StationSafetyExecutionResult(true, null, null)),
            async (_, cancellationToken) =>
            {
                safeStarted.TrySetResult();
                await releaseSafe.Task.WaitAsync(cancellationToken);
                return new StationSafetyExecutionResult(true, null, null);
            },
            (_, _) => ValueTask.FromResult(StationJobCancelExecutionResult.Success()),
            receiverStop.Token);

        var safeRequest = new StationSafeStopRequested(
            Guid.NewGuid(),
            $"safe-stop/{suffix}",
            agentId,
            stationId,
            "station-system.main",
            Guid.NewGuid(),
            "operation.main@0001",
            "operator.main",
            "Operator requested safe stop",
            DateTimeOffset.UtcNow);
        var safeTask = gateway.RequestSafeStopAsync(safeRequest).AsTask();
        await safeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var emergencyRequest = new EmergencyStopRequested(
            Guid.NewGuid(),
            $"emergency/{suffix}",
            agentId,
            stationId,
            "Guard opened",
            "operator.main",
            DateTimeOffset.UtcNow);
        var emergency = await gateway.RequestEmergencyStopAsync(emergencyRequest)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(emergency.Accepted);
        Assert.Equal(emergencyRequest.MessageId, emergency.RequestMessageId);
        Assert.False(safeTask.IsCompleted);

        releaseSafe.TrySetResult();
        var safe = await safeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(safe.Accepted);
        Assert.Equal(safeRequest.MessageId, safe.RequestMessageId);

        receiverStop.Cancel();
        await ObserveCancellationAsync(receiverLoop);
    }

    private Uri BrokerUri()
    {
        var builder = new UriBuilder("amqp", _rabbitMq.HostName, _rabbitMq.Port, "/")
        {
            UserName = _rabbitMq.UserName,
            Password = _rabbitMq.Password
        };
        return builder.Uri;
    }

    private static async Task DeclareJobTopologyAsync(
        Uri brokerUri,
        string coordinatorId,
        string stationId,
        string jobExchange,
        string eventExchange)
    {
        var factory = new ConnectionFactory { Uri = brokerUri };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(jobExchange, ExchangeType.Direct, true, false);
        await channel.ExchangeDeclareAsync(eventExchange, ExchangeType.Topic, true, false);
        var jobQueue = $"openlineops.station.{stationId}.jobs";
        await channel.QueueDeclareAsync(jobQueue, true, false, false);
        await channel.QueueBindAsync(jobQueue, jobExchange, $"station.{stationId}");
        var resultQueue = $"openlineops.coordinator.{coordinatorId}.station-results";
        await channel.QueueDeclareAsync(resultQueue, true, false, false);
        foreach (var type in new[]
                 {
                     nameof(StationJobAccepted),
                     nameof(StationJobProgressed),
                     nameof(StationJobCompleted)
                 })
        {
            await channel.QueueBindAsync(resultQueue, eventExchange, $"station.*.{type}");
        }
    }

    private static async Task DeclareSafetyCommandTopologyAsync(
        Uri brokerUri,
        string stationId,
        string commandExchange,
        string eventExchange)
    {
        var factory = new ConnectionFactory { Uri = brokerUri };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(commandExchange, ExchangeType.Direct, true, false);
        await channel.ExchangeDeclareAsync(eventExchange, ExchangeType.Topic, true, false);
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-max-priority"] = (byte)10
        };
        foreach (var suffix in new[] { "emergency-stop", "safe-stop", "job-cancel" })
        {
            var queue = $"openlineops.station.{stationId}.{suffix}";
            await channel.QueueDeclareAsync(queue, true, false, false, arguments);
            await channel.QueueBindAsync(
                queue,
                commandExchange,
                $"station.{stationId}.{suffix}");
        }
    }

    private static StationJobRequested JobRequest(string agentId, string stationId)
    {
        using var inputs = JsonDocument.Parse("{}");
        return new StationJobRequested(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"job/{Guid.NewGuid():N}",
            agentId,
            stationId,
            "station-system.main",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "operation.main@0001",
            1,
            "product.board",
            "serialNumber",
            "UNIT-001",
            null,
            null,
            "project.main",
            "application.main",
            "snapshot.main",
            "line.main",
            "topology.main",
            "operator.main",
            new string('a', 64),
            "operation.main",
            "flow.main",
            "flow-version.main",
            "configuration.main",
            "recipe.main",
            [],
            inputs.RootElement.Clone(),
            DateTimeOffset.UtcNow);
    }

    private static StationJobCompleted Completion(StationJobRequested request)
    {
        using var outputs = JsonDocument.Parse("{}");
        return new StationJobCompleted(
            Guid.NewGuid(),
            request.JobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.RuntimeSessionId,
            OpenLineOps.Runtime.Contracts.ExecutionStatus.Completed,
            OpenLineOps.Runtime.Contracts.ResultJudgement.Passed,
            outputs.RootElement.Clone(),
            0,
            0,
            0,
            [],
            [],
            [],
            [],
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (!await predicate())
        {
            await Task.Delay(25, timeoutSource.Token);
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private sealed class SystemUtcClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class ThreadSafeSafetyInboxStore : IStationSafetyInboxStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, StationSafetyInboxEntry> _entries =
            new(StringComparer.Ordinal);

        public ValueTask<StationSafetyInboxEntry?> GetAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return ValueTask.FromResult(
                    _entries.TryGetValue(idempotencyKey, out var entry) ? entry : null);
            }
        }

        public ValueTask<StationSafetyInboxEntry?> GetJobCancellationAsync(
            StationJobId jobId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return ValueTask.FromResult(_entries.Values.SingleOrDefault(entry =>
                    entry.TargetJobId == jobId.Value));
            }
        }

        public ValueTask<bool> TryBeginAsync(
            StationSafetyInboxEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_entries.ContainsKey(entry.IdempotencyKey))
                {
                    return ValueTask.FromResult(false);
                }

                _entries.Add(entry.IdempotencyKey, entry);
                return ValueTask.FromResult(true);
            }
        }

        public ValueTask<StationSafetyInboxEntry> CompleteAsync(
            string idempotencyKey,
            StationSafetyCommandKind commandKind,
            string requestSha256,
            string acknowledgementJson,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var current = _entries[idempotencyKey];
                if (current.CommandKind != commandKind
                    || !string.Equals(current.RequestSha256, requestSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Safety completion identity mismatch.");
                }

                var completed = current with
                {
                    AcknowledgementJson = acknowledgementJson,
                    CompletedAtUtc = completedAtUtc
                };
                _entries[idempotencyKey] = completed;
                return ValueTask.FromResult(completed);
            }
        }
    }
}
