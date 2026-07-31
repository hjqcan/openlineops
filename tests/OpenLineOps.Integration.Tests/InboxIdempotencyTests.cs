using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Domain.Messages;
using OpenLineOps.Integration.Infrastructure.Persistence;

namespace OpenLineOps.Integration.Tests;

public sealed class InboxIdempotencyTests
{
    [Fact]
    public async Task ExactRequestIsReplayedAfterColdRestartWithoutRepeatingBusiness()
    {
        using var database = new TemporaryIntegrationDatabase();
        var firstRequest = IntegrationTestData.Request(
            "request-replay",
            """{"quantity":4,"model":"A"}""");
        var firstHandler = new CountingWorkRequestHandler();
        WorkResponse expectedResponse;

        using (var firstStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            var firstService = new IdempotentWorkRequestService(firstStore, firstHandler);
            var first = await firstService.ProcessAsync(
                firstRequest,
                IntegrationTestData.Epoch.AddSeconds(2));

            Assert.Equal(WorkRequestProcessingOutcome.Processed, first.Outcome);
            expectedResponse = Assert.IsType<WorkResponse>(first.Response);
            Assert.Equal(1, firstHandler.InvocationCount);
        }

        var replayHandler = new CountingWorkRequestHandler();
        using (var restartedStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            var restartedService = new IdempotentWorkRequestService(
                restartedStore,
                replayHandler);
            var semanticallyIdenticalRequest = IntegrationTestData.Request(
                "request-replay",
                """
                {
                  "model": "A",
                  "quantity": 4
                }
                """);

            var replay = await restartedService.ProcessAsync(
                semanticallyIdenticalRequest,
                IntegrationTestData.Epoch.AddHours(1));

            Assert.Equal(WorkRequestProcessingOutcome.Replayed, replay.Outcome);
            Assert.Equal(expectedResponse, replay.Response);
            Assert.Equal(0, replayHandler.InvocationCount);
            var outbox = await restartedStore.GetOutboxAsync(expectedResponse.Id.Value);
            Assert.NotNull(outbox);
            Assert.Equal(firstRequest.Id.Value, outbox.CorrelationId);
            Assert.Equal(0, outbox.AttemptCount);
        }
    }

    [Fact]
    public async Task ReusedMessageIdWithDifferentContentIsAConflict()
    {
        using var database = new TemporaryIntegrationDatabase();
        using var store = new SqliteIntegrationStore(database.ConnectionString);
        var handler = new CountingWorkRequestHandler();
        var service = new IdempotentWorkRequestService(store, handler);
        var request = IntegrationTestData.Request("request-conflict");
        await service.ProcessAsync(request, IntegrationTestData.Epoch.AddSeconds(2));

        var conflictingRequest = IntegrationTestData.Request(
            "request-conflict",
            """{"model":"B","quantity":8}""");

        await Assert.ThrowsAsync<IntegrationMessageConflictException>(
            async () => await service.ProcessAsync(
                conflictingRequest,
                IntegrationTestData.Epoch.AddSeconds(3)));
        Assert.Equal(1, handler.InvocationCount);
        Assert.NotNull(await store.GetOutboxAsync($"response-{request.Id.Value}"));
    }

    [Fact]
    public async Task DuplicateWhileFirstRequestIsProcessingDoesNotEnterBusinessTwice()
    {
        using var database = new TemporaryIntegrationDatabase();
        using var store = new SqliteIntegrationStore(database.ConnectionString);
        var handler = new BlockingWorkRequestHandler();
        var service = new IdempotentWorkRequestService(store, handler);
        var request = IntegrationTestData.Request("request-in-progress");

        var firstTask = service.ProcessAsync(
                request,
                IntegrationTestData.Epoch.AddSeconds(2))
            .AsTask();
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicate = await service.ProcessAsync(
            request,
            IntegrationTestData.Epoch.AddSeconds(3));

        Assert.Equal(WorkRequestProcessingOutcome.InProgress, duplicate.Outcome);
        Assert.Null(duplicate.Response);
        Assert.Equal(1, handler.InvocationCount);

        handler.Release();
        var first = await firstTask;
        Assert.Equal(WorkRequestProcessingOutcome.Processed, first.Outcome);
        Assert.Equal(1, handler.InvocationCount);

        var replay = await service.ProcessAsync(
            request,
            IntegrationTestData.Epoch.AddSeconds(4));
        Assert.Equal(WorkRequestProcessingOutcome.Replayed, replay.Outcome);
        Assert.Equal(1, handler.InvocationCount);
    }
}
