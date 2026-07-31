using System.Collections.Concurrent;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Application.Serialization;
using OpenLineOps.Integration.Application.WorkOrders;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.Messages;
using OpenLineOps.Integration.Domain.WorkOrders;
using OpenLineOps.Integration.Infrastructure.Persistence;

namespace OpenLineOps.Integration.Tests;

public sealed class EnterpriseNetworkRecoveryTests
{
    private static readonly IntegrationInboxProcessingOptions ShortInboxLease =
        new(TimeSpan.FromMinutes(1));

    private static readonly IntegrationOutboxDispatchOptions DispatchOptions =
        new(
            maximumAttempts: 3,
            initialRetryDelay: TimeSpan.FromHours(1),
            maximumRetryDelay: TimeSpan.FromHours(2));

    [Fact]
    public async Task ExpiredInboxLeaseRecoversAfterCrashWithoutDuplicatingWorkOrder()
    {
        using var database = new TemporaryIntegrationDatabase();
        var request = IntegrationTestData.Request("request-crash-recovery");

        using (var firstStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            var interruptedHandler = new DurableWorkOrderHandler(
                firstStore,
                interruptAfterBusinessCommit: true);
            var service = new IdempotentWorkRequestService(
                firstStore,
                interruptedHandler,
                ShortInboxLease);

            await Assert.ThrowsAsync<SimulatedProcessInterruptionException>(
                async () => await service.ProcessAsync(
                    request,
                    IntegrationTestData.Epoch.AddSeconds(2)));

            var interruptedOrder = await new WorkOrderService(firstStore)
                .GetAsync(request.WorkOrderId);
            Assert.Single(Assert.IsType<WorkOrder>(interruptedOrder).Facts);
            var firstClaim = Assert.Single(
                await firstStore.ListClaimAuditAsync(request.Id.Value));
            Assert.False(firstClaim.Reclaimed);
            Assert.Equal(request.SourceSystem, firstClaim.ActorId);
        }

        using var restartedStore = new SqliteIntegrationStore(database.ConnectionString);
        var recoveredHandler = new DurableWorkOrderHandler(
            restartedStore,
            interruptAfterBusinessCommit: false);
        var restartedService = new IdempotentWorkRequestService(
            restartedStore,
            recoveredHandler,
            ShortInboxLease);
        var recovered = await restartedService.ProcessAsync(
            request,
            IntegrationTestData.Epoch.AddMinutes(2));

        Assert.Equal(WorkRequestProcessingOutcome.Processed, recovered.Outcome);
        Assert.NotNull(recovered.Response);
        Assert.Equal(1, recoveredHandler.InvocationCount);
        var recoveredOrder = await new WorkOrderService(restartedStore)
            .GetAsync(request.WorkOrderId);
        Assert.Single(Assert.IsType<WorkOrder>(recoveredOrder).Facts);
        Assert.NotNull(await restartedStore.GetOutboxAsync(recovered.Response.Id.Value));

        var claims = await restartedStore.ListClaimAuditAsync(request.Id.Value);
        Assert.Equal(2, claims.Count);
        Assert.False(claims[0].Reclaimed);
        Assert.True(claims[1].Reclaimed);
        Assert.NotEqual(claims[0].ProcessingToken, claims[1].ProcessingToken);
        Assert.All(
            claims,
            claim => Assert.Equal(request.SourceSystem, claim.ActorId));

        var replay = await restartedService.ProcessAsync(
            request,
            IntegrationTestData.Epoch.AddMinutes(3));
        Assert.Equal(WorkRequestProcessingOutcome.Replayed, replay.Outcome);
        Assert.Equal(recovered.Response, replay.Response);
        Assert.Equal(1, recoveredHandler.InvocationCount);
    }

    [Fact]
    public async Task TwentyFourHourOutageRestartDeadLetterAndReplayLoseNoResults()
    {
        using var database = new TemporaryIntegrationDatabase();
        var firstRequest = IntegrationTestData.Request("request-offline-1");
        var secondRequest = IntegrationTestData.Request(
            "request-offline-2",
            minuteOffset: 1);
        var clock = new ManualTimeProvider(
            IntegrationTestData.Epoch.AddMinutes(10));
        var connector = new DeterministicEnterpriseConnector
        {
            IsReady = false,
            RejectBeforeAcceptance = true
        };

        await CreateOrdersAndResponsesAsync(
            database.ConnectionString,
            firstRequest,
            secondRequest);

        using (var offlineStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            var dispatcher = new IntegrationOutboxDispatcher(
                offlineStore,
                connector,
                DispatchOptions);
            Assert.Equal(
                0,
                await dispatcher.DispatchAsync(10, clock.GetUtcNow()));

            clock.Advance(TimeSpan.FromHours(12));
            Assert.Equal(
                0,
                await dispatcher.DispatchAsync(10, clock.GetUtcNow()));
            Assert.Equal(0, connector.TransportAttemptCount);
        }

        clock.Advance(TimeSpan.FromHours(12));
        using (var restartedOfflineStore =
               new SqliteIntegrationStore(database.ConnectionString))
        {
            var dispatcher = new IntegrationOutboxDispatcher(
                restartedOfflineStore,
                connector,
                DispatchOptions);
            Assert.Equal(
                0,
                await dispatcher.DispatchAsync(10, clock.GetUtcNow()));
            Assert.Equal(0, connector.TransportAttemptCount);
            await AssertAttemptsAsync(
                restartedOfflineStore,
                firstRequest,
                expectedAttempts: 0);
            await AssertAttemptsAsync(
                restartedOfflineStore,
                secondRequest,
                expectedAttempts: 0);
        }

        connector.IsReady = true;
        await ExhaustToDeadLetterAsync(
            database.ConnectionString,
            connector,
            clock,
            firstRequest);
        await ExhaustToDeadLetterAsync(
            database.ConnectionString,
            connector,
            clock,
            secondRequest);
        Assert.Equal(6, connector.TransportAttemptCount);
        Assert.Empty(connector.BusinessEffectMessageIds);

        connector.RejectBeforeAcceptance = false;
        connector.LoseFirstAcknowledgement = true;
        await ReplayAndDeliverAsync(
            database.ConnectionString,
            connector,
            clock,
            secondRequest,
            "integration-engineer",
            "Enterprise link validated after the isolation window.");
        await ReplayAndDeliverAsync(
            database.ConnectionString,
            connector,
            clock,
            firstRequest,
            "integration-engineer",
            "Earlier response replayed after dependent checks.");

        Assert.Equal(
            [
                ResponseId(secondRequest),
                ResponseId(firstRequest)
            ],
            connector.BusinessEffectMessageIds);
        Assert.Equal(2, connector.BusinessEffectCount);
        Assert.Equal(5, connector.AttemptsFor(ResponseId(firstRequest)));
        Assert.Equal(5, connector.AttemptsFor(ResponseId(secondRequest)));
        const string conflictingPayload = """{"accepted":false}""";
        await Assert.ThrowsAsync<IntegrationConnectorMessageConflictException>(
            async () => await connector.SendAsync(new IntegrationOutboundMessage(
                99,
                ResponseId(firstRequest),
                firstRequest.Id.Value,
                IntegrationMessageCodec.ComputeSha256(conflictingPayload),
                conflictingPayload,
                clock.GetUtcNow(),
                0,
                clock.GetUtcNow())));
        Assert.Equal(2, connector.BusinessEffectCount);

        using var finalStore = new SqliteIntegrationStore(database.ConnectionString);
        await AssertDeliveredAsync(finalStore, firstRequest);
        await AssertDeliveredAsync(finalStore, secondRequest);
        await AssertFailureAuditAsync(finalStore, firstRequest);
        await AssertFailureAuditAsync(finalStore, secondRequest);
        Assert.Empty(await finalStore.ListDeadLettersAsync(10));
        Assert.Single(
            Assert.IsType<WorkOrder>(
                await new WorkOrderService(finalStore)
                    .GetAsync(firstRequest.WorkOrderId))
                .Facts);
        Assert.Single(
            Assert.IsType<WorkOrder>(
                await new WorkOrderService(finalStore)
                    .GetAsync(secondRequest.WorkOrderId))
                .Facts);
    }

    private static async Task CreateOrdersAndResponsesAsync(
        string connectionString,
        WorkRequest firstRequest,
        WorkRequest secondRequest)
    {
        using var store = new SqliteIntegrationStore(connectionString);
        var handler = new DurableWorkOrderHandler(
            store,
            interruptAfterBusinessCommit: false);
        var service = new IdempotentWorkRequestService(store, handler);

        var first = await service.ProcessAsync(
            firstRequest,
            IntegrationTestData.Epoch.AddSeconds(2));
        var second = await service.ProcessAsync(
            secondRequest,
            IntegrationTestData.Epoch.AddMinutes(1).AddSeconds(2));
        var duplicate = await service.ProcessAsync(
            firstRequest,
            IntegrationTestData.Epoch.AddMinutes(2));

        Assert.Equal(WorkRequestProcessingOutcome.Processed, first.Outcome);
        Assert.Equal(WorkRequestProcessingOutcome.Processed, second.Outcome);
        Assert.Equal(WorkRequestProcessingOutcome.Replayed, duplicate.Outcome);
        Assert.Equal(2, handler.InvocationCount);

        var conflictingRequest = IntegrationTestData.Request(
            firstRequest.Id.Value,
            """{"model":"changed","quantity":99}""");
        await Assert.ThrowsAsync<IntegrationMessageConflictException>(
            async () => await service.ProcessAsync(
                conflictingRequest,
                IntegrationTestData.Epoch.AddMinutes(3)));
        Assert.Equal(2, handler.InvocationCount);
    }

    private static async Task ExhaustToDeadLetterAsync(
        string connectionString,
        DeterministicEnterpriseConnector connector,
        ManualTimeProvider clock,
        WorkRequest request)
    {
        for (var expectedAttempt = 1; expectedAttempt <= 3; expectedAttempt++)
        {
            using var store = new SqliteIntegrationStore(connectionString);
            var dispatcher = new IntegrationOutboxDispatcher(
                store,
                connector,
                DispatchOptions);
            Assert.Equal(
                0,
                await dispatcher.DispatchAsync(10, clock.GetUtcNow()));
            await AssertAttemptsAsync(store, request, expectedAttempt);
            if (expectedAttempt == 1)
            {
                Assert.Equal(
                    0,
                    await dispatcher.DispatchAsync(
                        10,
                        clock.GetUtcNow().AddMinutes(59)));
            }

            if (expectedAttempt < 3)
            {
                clock.Advance(
                    expectedAttempt == 1
                        ? TimeSpan.FromHours(1)
                        : TimeSpan.FromHours(2));
            }
        }

        using var verificationStore =
            new SqliteIntegrationStore(connectionString);
        var snapshot = Assert.IsType<IntegrationOutboxSnapshot>(
            await verificationStore.GetOutboxAsync(ResponseId(request)));
        Assert.NotNull(snapshot.DeadLetteredAtUtc);
    }

    private static async Task ReplayAndDeliverAsync(
        string connectionString,
        DeterministicEnterpriseConnector connector,
        ManualTimeProvider clock,
        WorkRequest request,
        string actorId,
        string reason)
    {
        var messageId = ResponseId(request);
        using (var replayStore = new SqliteIntegrationStore(connectionString))
        {
            var replay = new IntegrationOutboxReplayService(
                replayStore,
                new FixedReplayAuthorizer(authorized: true));
            await replay.ReplayAsync(new ManualOutboxReplayRequest(
                messageId,
                actorId,
                reason,
                clock.GetUtcNow()));
            var dispatcher = new IntegrationOutboxDispatcher(
                replayStore,
                connector,
                DispatchOptions);

            Assert.Equal(
                0,
                await dispatcher.DispatchAsync(10, clock.GetUtcNow()));
            var ambiguous = Assert.IsType<IntegrationOutboxSnapshot>(
                await replayStore.GetOutboxAsync(messageId));
            Assert.Equal(1, ambiguous.AttemptCount);
            Assert.Null(ambiguous.DeliveredAtUtc);
            Assert.Null(ambiguous.DeadLetteredAtUtc);
        }

        clock.Advance(TimeSpan.FromHours(1));
        using var restartedStore =
            new SqliteIntegrationStore(connectionString);
        var restartedDispatcher = new IntegrationOutboxDispatcher(
            restartedStore,
            connector,
            DispatchOptions);
        Assert.Equal(
            1,
            await restartedDispatcher.DispatchAsync(10, clock.GetUtcNow()));

        var audit = Assert.Single(
            await restartedStore.ListReplayAuditAsync(messageId));
        Assert.Equal(actorId, audit.ActorId);
        Assert.Equal(reason, audit.Reason);
        Assert.Equal(3, audit.PreviousAttemptCount);
    }

    private static async Task AssertAttemptsAsync(
        SqliteIntegrationStore store,
        WorkRequest request,
        int expectedAttempts)
    {
        var snapshot = Assert.IsType<IntegrationOutboxSnapshot>(
            await store.GetOutboxAsync(ResponseId(request)));
        Assert.Equal(expectedAttempts, snapshot.AttemptCount);
        Assert.Null(snapshot.DeliveredAtUtc);
    }

    private static async Task AssertDeliveredAsync(
        SqliteIntegrationStore store,
        WorkRequest request)
    {
        var snapshot = Assert.IsType<IntegrationOutboxSnapshot>(
            await store.GetOutboxAsync(ResponseId(request)));
        Assert.NotNull(snapshot.DeliveredAtUtc);
        Assert.Null(snapshot.DeadLetteredAtUtc);
    }

    private static async Task AssertFailureAuditAsync(
        SqliteIntegrationStore store,
        WorkRequest request)
    {
        var audit = await store.ListFailureAuditAsync(ResponseId(request));
        Assert.Equal(4, audit.Count);
        Assert.Equal([1, 2, 3, 1], audit.Select(static item => item.AttemptCount));
        Assert.Equal(
            [false, false, true, false],
            audit.Select(static item => item.DeadLettered));
        Assert.All(
            audit,
            item => Assert.True(item.NextAttemptAtUtc >= item.FailedAtUtc));
    }

    private static string ResponseId(WorkRequest request) =>
        $"response-{request.Id.Value}";

    private sealed class DurableWorkOrderHandler(
        IWorkOrderFactStore factStore,
        bool interruptAfterBusinessCommit) : IWorkRequestHandler
    {
        private readonly WorkOrderService _workOrders = new(factStore);
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async ValueTask<WorkResponse> HandleAsync(
            WorkRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            await _workOrders.CreateAsync(
                new CreateWorkOrderCommand(
                    request.WorkOrderId,
                    "model-a",
                    4,
                    new WorkOrderFactId($"fact-{request.Id.Value}"),
                    request.OccurredAtUtc,
                    request.SourceSystem),
                cancellationToken);
            if (interruptAfterBusinessCommit)
            {
                throw new SimulatedProcessInterruptionException();
            }

            return IntegrationTestData.ResponseFor(request);
        }
    }

    private sealed class DeterministicEnterpriseConnector :
        IIntegrationConnector,
        IIntegrationConnectorReadiness
    {
        private readonly ConcurrentDictionary<string, string> _acceptedHashes =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _attempts =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _lostAcknowledgements =
            new(StringComparer.Ordinal);
        private readonly List<string> _businessEffectMessageIds = [];
        private readonly object _effectsLock = new();
        private int _transportAttemptCount;

        public bool IsReady { get; set; }

        public string? UnavailabilityReason =>
            IsReady ? null : "Enterprise network is isolated.";

        public bool RejectBeforeAcceptance { get; set; }

        public bool LoseFirstAcknowledgement { get; set; }

        public int TransportAttemptCount => Volatile.Read(ref _transportAttemptCount);

        public int BusinessEffectCount
        {
            get
            {
                lock (_effectsLock)
                {
                    return _businessEffectMessageIds.Count;
                }
            }
        }

        public IReadOnlyList<string> BusinessEffectMessageIds
        {
            get
            {
                lock (_effectsLock)
                {
                    return _businessEffectMessageIds.ToArray();
                }
            }
        }

        public int AttemptsFor(string messageId) =>
            _attempts.GetValueOrDefault(messageId);

        public ValueTask SendAsync(
            IntegrationOutboundMessage message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _transportAttemptCount);
            _attempts.AddOrUpdate(
                message.MessageId,
                addValue: 1,
                static (_, current) => checked(current + 1));
            if (!IsReady || RejectBeforeAcceptance)
            {
                return ValueTask.FromException(
                    new IOException("Enterprise transport is unavailable."));
            }

            if (_acceptedHashes.TryGetValue(message.MessageId, out var acceptedHash))
            {
                if (!string.Equals(
                        acceptedHash,
                        message.ContentSha256,
                        StringComparison.Ordinal))
                {
                    return ValueTask.FromException(
                        new IntegrationConnectorMessageConflictException(
                            $"Enterprise message id '{message.MessageId}' was reused with different content."));
                }
            }
            else if (_acceptedHashes.TryAdd(
                         message.MessageId,
                         message.ContentSha256))
            {
                lock (_effectsLock)
                {
                    _businessEffectMessageIds.Add(message.MessageId);
                }
            }

            if (LoseFirstAcknowledgement
                && _lostAcknowledgements.TryAdd(message.MessageId, 0))
            {
                return ValueTask.FromException(
                    new IOException(
                        "Enterprise endpoint committed before the acknowledgement was lost."));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc)
        : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
                duration,
                TimeSpan.Zero);
            _utcNow = _utcNow.Add(duration);
        }
    }

    private sealed class SimulatedProcessInterruptionException : IOException;
}
