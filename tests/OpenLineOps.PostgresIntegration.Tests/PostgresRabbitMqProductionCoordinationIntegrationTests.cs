using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Time;
using OpenLineOps.Runtime.Infrastructure.Transport;
using RabbitMQ.Client;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(ProductionInfrastructureContainerGroup.Name)]
public sealed class PostgresRabbitMqProductionCoordinationIntegrationTests(
    PostgresContainerFixture postgres,
    RabbitMqContainerFixture rabbitMq)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [ProductionInfrastructureIntegrationFact]
    public async Task DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station-{suffix}";
        var agentId = $"agent-{suffix}";
        var coordinatorId = $"coordinator-{suffix}";
        var jobExchange = $"openlineops.integration.jobs.{suffix}";
        var eventExchange = $"openlineops.integration.events.{suffix}";
        var brokerUri = BrokerUri();
        await DeclareTopologyAsync(
            brokerUri,
            coordinatorId,
            agentId,
            stationId,
            jobExchange,
            eventExchange);

        var request = JobRequest(agentId, stationId);
        var leaseChange = StationDispatchMessageIdentity.CreateLeaseGranted(
            request,
            Assert.Single(request.ResourceFences));
        using (var admittingStore = new PostgreSqlProductionCoordinationStore(
                   postgres.ConnectionString))
        {
            Assert.True(await admittingStore.TryEnqueueAsync(request, [leaseChange]));
            Assert.NotEmpty(await admittingStore.ListPendingAsync(32));
        }

        using var restartedStore = new PostgreSqlProductionCoordinationStore(
            postgres.ConnectionString);
        var coordinatorOptions = new StationCoordinatorTransportOptions
        {
            BrokerUri = brokerUri.AbsoluteUri,
            RequireTls = false,
            CoordinatorId = coordinatorId,
            JobExchange = jobExchange,
            EventExchange = eventExchange,
            Deployments =
            [
                new StationDeploymentOptions
                {
                    ProjectId = request.ProjectId,
                    ApplicationId = request.ApplicationId,
                    AgentId = agentId,
                    StationId = stationId,
                    StationSystemId = request.StationSystemId
                }
            ]
        };
        await using var coordinator = new RabbitMqStationCoordinatorTransport(
            coordinatorOptions,
            restartedStore,
            new PostgreSqlAgentPresenceRepository(postgres.ConnectionString),
            ArtifactFreeStationArtifactReceiptVerifier.Instance,
            new SystemClock());
        await using var agent = new RabbitMqStationTransport(
            new RabbitMqStationTransportOptions(
                brokerUri,
                agentId,
                stationId,
                request.StationSystemId,
                jobExchange,
                eventExchange,
                PrefetchCount: 1,
                MaximumConcurrentJobs: 1,
                RequireTls: false));

        var physicalExecutions = 0;
        var observedLeaseChanges = 0;
        using var stop = new CancellationTokenSource();
        var resultLoop = coordinator.RunResultInboxAsync(
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            stop.Token);
        var agentLoop = agent.RunAsync(
            async (received, cancellationToken) =>
            {
                Assert.Equal(request.IdempotencyKey, received.IdempotencyKey);
                Interlocked.Increment(ref physicalExecutions);
                var acceptedAtUtc = DateTimeOffset.UtcNow;
                await agent.PublishAsync(
                    nameof(StationJobAccepted),
                    JsonSerializer.Serialize(
                        new StationJobAccepted(
                            Guid.NewGuid(),
                            received.JobId,
                            received.IdempotencyKey,
                            received.AgentId,
                            received.StationId,
                            acceptedAtUtc),
                        JsonOptions),
                    cancellationToken);
                await agent.PublishAsync(
                    nameof(StationJobCompleted),
                    JsonSerializer.Serialize(
                        Completion(received, acceptedAtUtc.AddMilliseconds(1)),
                        JsonOptions),
                    cancellationToken);
            },
            (received, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(leaseChange.MessageId, received.MessageId);
                Interlocked.Increment(ref observedLeaseChanges);
                return ValueTask.CompletedTask;
            },
            stop.Token);

        await DrainOutboxAsync(restartedStore, coordinator, stop.Token);
        await WaitUntilAsync(
            async () => await restartedStore.GetCompletionAsync(request.IdempotencyKey) is not null,
            TimeSpan.FromSeconds(15));

        Assert.Equal(1, Volatile.Read(ref observedLeaseChanges));
        Assert.Equal(1, Volatile.Read(ref physicalExecutions));
        Assert.Empty(await restartedStore.ListPendingAsync(32));

        stop.Cancel();
        await ObserveCancellationAsync(resultLoop);
        await ObserveCancellationAsync(agentLoop);

        using var coldStore = new PostgreSqlProductionCoordinationStore(postgres.ConnectionString);
        var frozenCompletion = Assert.IsType<StationJobCompleted>(
            await coldStore.GetCompletionAsync(request.IdempotencyKey));
        Assert.Equal(request.JobId, frozenCompletion.JobId);
        Assert.Equal(request.RuntimeSessionId, frozenCompletion.RuntimeSessionId);
        Assert.Empty(await coldStore.ListPendingAsync(32));
    }

    private static async Task DrainOutboxAsync(
        PostgreSqlProductionCoordinationStore store,
        RabbitMqStationCoordinatorTransport coordinator,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = await store.ListPendingAsync(32, cancellationToken);
            if (pending.Count == 0)
            {
                return;
            }

            foreach (var item in pending)
            {
                switch (item.Kind)
                {
                    case nameof(ResourceLeaseChanged):
                        await coordinator.PublishAsync(
                            JsonSerializer.Deserialize<ResourceLeaseChanged>(
                                item.PayloadJson,
                                JsonOptions)
                            ?? throw new InvalidDataException(
                                "Durable resource-lease outbox payload is empty."),
                            cancellationToken);
                        break;
                    case nameof(StationJobRequested):
                        await coordinator.PublishAsync(
                            JsonSerializer.Deserialize<StationJobRequested>(
                                item.PayloadJson,
                                JsonOptions)
                            ?? throw new InvalidDataException(
                                "Durable Station-job outbox payload is empty."),
                            cancellationToken);
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unsupported durable Station outbox kind '{item.Kind}'.");
                }

                await store.MarkPublishedAsync(item.MessageId, cancellationToken);
            }
        }
    }

    private Uri BrokerUri()
    {
        var builder = new UriBuilder("amqp", rabbitMq.HostName, rabbitMq.Port, "/")
        {
            UserName = rabbitMq.UserName,
            Password = rabbitMq.Password
        };
        return builder.Uri;
    }

    private static async Task DeclareTopologyAsync(
        Uri brokerUri,
        string coordinatorId,
        string agentId,
        string stationId,
        string jobExchange,
        string eventExchange)
    {
        var factory = new ConnectionFactory { Uri = brokerUri };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(jobExchange, ExchangeType.Direct, true, false);
        await channel.ExchangeDeclareAsync(eventExchange, ExchangeType.Topic, true, false);
        var jobQueue = StationTransportRoute.JobQueue(agentId, stationId);
        await channel.QueueDeclareAsync(jobQueue, true, false, false);
        await channel.QueueBindAsync(
            jobQueue,
            jobExchange,
            StationTransportRoute.Job(agentId, stationId));
        var resultQueue = $"openlineops.coordinator.{coordinatorId}.station-results";
        await channel.QueueDeclareAsync(resultQueue, true, false, false);
        foreach (var type in new[]
                 {
                     nameof(StationJobAccepted),
                     nameof(StationJobCompleted)
                 })
        {
            await channel.QueueBindAsync(
                resultQueue,
                eventExchange,
                StationTransportRoute.EventPattern(type));
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
            [new StationResourceFence(
                "Station",
                "station-system.main",
                1,
                DateTimeOffset.UtcNow.AddMinutes(5))],
            inputs.RootElement.Clone(),
            DateTimeOffset.UtcNow);
    }

    private static StationJobCompleted Completion(
        StationJobRequested request,
        DateTimeOffset completedAtUtc)
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
            completedAtUtc);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
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
}
