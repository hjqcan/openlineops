using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.WorkOrders;
using OpenLineOps.Integration.Infrastructure.Persistence;

namespace OpenLineOps.Integration.Tests;

public sealed class SqliteIntegrationStoreTests
{
    [Fact]
    public async Task WorkOrderFactsAreAppendOnlyExactlyReplayableAndPersisted()
    {
        using var database = new TemporaryIntegrationDatabase();
        var order = WorkOrder.Create(
            new WorkOrderId("order-facts"),
            "product-c",
            25,
            new WorkOrderFactId("fact-facts-created"),
            IntegrationTestData.Epoch,
            "planner");
        order.Release(
            new WorkOrderFactId("fact-facts-released"),
            IntegrationTestData.Epoch.AddMinutes(1),
            "supervisor");

        using (var firstStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            await firstStore.AppendAsync(order.Facts);
            await firstStore.AppendAsync(order.Facts);
            Assert.Equal(2, (await firstStore.ListAsync(order.Id)).Count);
        }

        using (var restartedStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            var persisted = await restartedStore.ListAsync(order.Id);
            Assert.Equal(order.Facts, persisted);

            var conflictingFact = new WorkOrderFact(
                order.Facts[1].Id,
                order.Id,
                2,
                WorkOrderFactKind.Released,
                WorkOrderStatus.Released,
                order.Facts[1].OccurredAtUtc,
                "different-actor");
            await Assert.ThrowsAsync<IntegrationMessageConflictException>(
                async () => await restartedStore.AppendAsync([conflictingFact]));

            var sequenceConflict = new WorkOrderFact(
                new WorkOrderFactId("different-fact-id"),
                order.Id,
                2,
                WorkOrderFactKind.Released,
                WorkOrderStatus.Released,
                order.Facts[1].OccurredAtUtc,
                order.Facts[1].ActorId);
            await Assert.ThrowsAsync<IntegrationMessageConflictException>(
                async () => await restartedStore.AppendAsync([sequenceConflict]));

            var sequenceGap = new WorkOrderFact(
                new WorkOrderFactId("fact-with-gap"),
                order.Id,
                4,
                WorkOrderFactKind.Held,
                WorkOrderStatus.Held,
                order.Facts[1].OccurredAtUtc.AddMinutes(1),
                "quality",
                reason: "Investigation");
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await restartedStore.AppendAsync([sequenceGap]));
            Assert.Equal(order.Facts, await restartedStore.ListAsync(order.Id));
        }
    }

    [Fact]
    public async Task FailedDeliveryPreservesSequenceAndBackoffAcrossColdRestart()
    {
        using var database = new TemporaryIntegrationDatabase();
        var firstRequest = IntegrationTestData.Request("request-sequence-1");
        var secondRequest = IntegrationTestData.Request(
            "request-sequence-2",
            minuteOffset: 1);
        var options = new IntegrationOutboxDispatchOptions(
            maximumAttempts: 3,
            initialRetryDelay: TimeSpan.FromSeconds(1),
            maximumRetryDelay: TimeSpan.FromSeconds(10));
        var dispatchAt = IntegrationTestData.Epoch.AddMinutes(10);

        using (var firstStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            var service = new IdempotentWorkRequestService(
                firstStore,
                new CountingWorkRequestHandler());
            await service.ProcessAsync(firstRequest, IntegrationTestData.Epoch.AddSeconds(2));
            await service.ProcessAsync(secondRequest, IntegrationTestData.Epoch.AddMinutes(1));

            var connector = new AlwaysFailingConnector();
            var dispatcher = new IntegrationOutboxDispatcher(firstStore, connector, options);

            Assert.Equal(0, await dispatcher.DispatchAsync(10, dispatchAt));
            Assert.Equal([$"response-{firstRequest.Id.Value}"], connector.AttemptedMessageIds);
            var first = Assert.IsType<IntegrationOutboxSnapshot>(
                await firstStore.GetOutboxAsync($"response-{firstRequest.Id.Value}"));
            var second = Assert.IsType<IntegrationOutboxSnapshot>(
                await firstStore.GetOutboxAsync($"response-{secondRequest.Id.Value}"));
            Assert.Equal(1, first.AttemptCount);
            Assert.Equal(dispatchAt.AddSeconds(1), first.NextAttemptAtUtc);
            Assert.Equal(0, second.AttemptCount);
        }

        using (var restartedStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            var connector = new AlwaysFailingConnector();
            var dispatcher = new IntegrationOutboxDispatcher(restartedStore, connector, options);

            Assert.Equal(
                0,
                await dispatcher.DispatchAsync(10, dispatchAt.AddMilliseconds(500)));
            Assert.Empty(connector.AttemptedMessageIds);

            Assert.Equal(0, await dispatcher.DispatchAsync(10, dispatchAt.AddSeconds(1)));
            Assert.Equal(
                [$"response-{firstRequest.Id.Value}"],
                connector.AttemptedMessageIds);
            var first = Assert.IsType<IntegrationOutboxSnapshot>(
                await restartedStore.GetOutboxAsync($"response-{firstRequest.Id.Value}"));
            var second = Assert.IsType<IntegrationOutboxSnapshot>(
                await restartedStore.GetOutboxAsync($"response-{secondRequest.Id.Value}"));
            Assert.Equal(2, first.AttemptCount);
            Assert.Equal(dispatchAt.AddSeconds(3), first.NextAttemptAtUtc);
            Assert.Equal(0, second.AttemptCount);
            Assert.Null(first.DeliveredAtUtc);
            Assert.Null(second.DeliveredAtUtc);
        }
    }

    [Fact]
    public async Task DeadLetterRequiresAuthorizationAndManualReplayIsAudited()
    {
        using var database = new TemporaryIntegrationDatabase();
        var request = IntegrationTestData.Request("request-dead-letter");
        var messageId = $"response-{request.Id.Value}";
        var options = new IntegrationOutboxDispatchOptions(
            maximumAttempts: 3,
            initialRetryDelay: TimeSpan.FromSeconds(1),
            maximumRetryDelay: TimeSpan.FromSeconds(10));
        var dispatchAt = IntegrationTestData.Epoch.AddMinutes(20);

        using (var store = new SqliteIntegrationStore(database.ConnectionString))
        {
            var service = new IdempotentWorkRequestService(
                store,
                new CountingWorkRequestHandler());
            await service.ProcessAsync(request, IntegrationTestData.Epoch.AddSeconds(2));
            var dispatcher = new IntegrationOutboxDispatcher(
                store,
                new AlwaysFailingConnector(),
                options);

            Assert.Equal(0, await dispatcher.DispatchAsync(1, dispatchAt));
            Assert.Equal(0, await dispatcher.DispatchAsync(1, dispatchAt.AddSeconds(1)));
            Assert.Equal(0, await dispatcher.DispatchAsync(1, dispatchAt.AddSeconds(3)));
            var deadLetter = Assert.IsType<IntegrationOutboxSnapshot>(
                await store.GetOutboxAsync(messageId));
            Assert.Equal(3, deadLetter.AttemptCount);
            Assert.Equal(dispatchAt.AddSeconds(3), deadLetter.DeadLetteredAtUtc);
        }

        using (var restartedStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            var replayAt = dispatchAt.AddMinutes(1);
            var requestReplay = new ManualOutboxReplayRequest(
                messageId,
                "integration-supervisor",
                "Enterprise endpoint has recovered.",
                replayAt);
            var denied = new IntegrationOutboxReplayService(
                restartedStore,
                new FixedReplayAuthorizer(authorized: false));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                async () => await denied.ReplayAsync(requestReplay));
            Assert.Empty(await restartedStore.ListReplayAuditAsync(messageId));

            var authorized = new IntegrationOutboxReplayService(
                restartedStore,
                new FixedReplayAuthorizer(authorized: true));
            await authorized.ReplayAsync(requestReplay);

            var requeued = Assert.IsType<IntegrationOutboxSnapshot>(
                await restartedStore.GetOutboxAsync(messageId));
            Assert.Equal(0, requeued.AttemptCount);
            Assert.Equal(replayAt, requeued.NextAttemptAtUtc);
            Assert.Null(requeued.DeadLetteredAtUtc);
            Assert.Null(requeued.DeliveredAtUtc);
            var ready = Assert.Single(await restartedStore.ListReadyAsync(1, replayAt));
            Assert.Equal(messageId, ready.MessageId);

            var audit = Assert.Single(await restartedStore.ListReplayAuditAsync(messageId));
            Assert.Equal("integration-supervisor", audit.ActorId);
            Assert.Equal("Enterprise endpoint has recovered.", audit.Reason);
            Assert.Equal(replayAt, audit.ReplayedAtUtc);
            Assert.Equal(3, audit.PreviousAttemptCount);
        }
    }
}
