using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Serialization;
using OpenLineOps.Integration.Domain.Messages;
using OpenLineOps.Integration.Infrastructure.Persistence;

namespace OpenLineOps.Integration.Tests;

public sealed class InboxIdempotencyTests
{
    [Fact]
    public async Task ExpiredClaimCannotCompleteAfterANewerClaimReclaimsTheRequest()
    {
        using var database = new TemporaryIntegrationDatabase();
        using var store = new SqliteIntegrationStore(database.ConnectionString);
        var request = IntegrationTestData.Request("request-fenced-completion");
        var requestJson = IntegrationMessageCodec.Encode(request);
        var requestHash = IntegrationMessageCodec.ComputeSha256(requestJson);
        var response = IntegrationTestData.ResponseFor(request);
        var responseJson = IntegrationMessageCodec.Encode(response);
        var firstClaim = new IntegrationInboundMessage(
            request.Id.Value,
            requestHash,
            requestJson,
            IntegrationTestData.Epoch,
            request.SourceSystem,
            "claim-old",
            IntegrationTestData.Epoch.AddMinutes(1));
        var newerClaim = new IntegrationInboundMessage(
            request.Id.Value,
            requestHash,
            requestJson,
            IntegrationTestData.Epoch.AddMinutes(1),
            request.SourceSystem,
            "claim-new",
            IntegrationTestData.Epoch.AddMinutes(2));

        Assert.Equal(
            IntegrationInboxDisposition.Started,
            (await store.TryBeginAsync(firstClaim)).Disposition);
        Assert.Equal(
            IntegrationInboxDisposition.Started,
            (await store.TryBeginAsync(newerClaim)).Disposition);

        await Assert.ThrowsAsync<IntegrationInboxLeaseLostException>(
            async () => await store.CompleteAndEnqueueResponseAsync(
                new IntegrationInboxCompletion(
                    request.Id.Value,
                    requestHash,
                    response.Id.Value,
                    responseJson,
                    response.OccurredAtUtc,
                    firstClaim.ProcessingToken)));
        Assert.Null(await store.GetOutboxAsync(response.Id.Value));

        await store.CompleteAndEnqueueResponseAsync(
            new IntegrationInboxCompletion(
                request.Id.Value,
                requestHash,
                response.Id.Value,
                responseJson,
                response.OccurredAtUtc,
                newerClaim.ProcessingToken));
        Assert.NotNull(await store.GetOutboxAsync(response.Id.Value));
    }

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

        await using (var connection =
                     new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT response_json, response_sha256
                FROM integration_inbox
                WHERE message_id = $message_id;
                """;
            command.Parameters.AddWithValue("$message_id", firstRequest.Id.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(
                IntegrationMessageCodec.ComputeSha256(reader.GetString(0)),
                reader.GetString(1));
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

    [Theory]
    [InlineData("inbox-response-json")]
    [InlineData("inbox-response-hash")]
    [InlineData("outbox-correlation")]
    [InlineData("outbox-payload")]
    [InlineData("outbox-hash")]
    [InlineData("outbox-missing")]
    public async Task CompletedReplayFailsClosedWhenPersistedResponseEvidenceIsTampered(
        string mutation)
    {
        using var database = new TemporaryIntegrationDatabase();
        var request = IntegrationTestData.Request($"request-tamper-{mutation}");
        using (var initialStore = new SqliteIntegrationStore(database.ConnectionString))
        {
            await new IdempotentWorkRequestService(
                    initialStore,
                    new CountingWorkRequestHandler())
                .ProcessAsync(request, IntegrationTestData.Epoch.AddSeconds(2));
        }

        await TamperCompletedEvidenceAsync(database.ConnectionString, mutation);

        var replayHandler = new CountingWorkRequestHandler();
        using var restartedStore = new SqliteIntegrationStore(database.ConnectionString);
        var service = new IdempotentWorkRequestService(restartedStore, replayHandler);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await service.ProcessAsync(
                request,
                IntegrationTestData.Epoch.AddMinutes(1)));
        Assert.Equal(0, replayHandler.InvocationCount);
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

    private static async Task TamperCompletedEvidenceAsync(
        string connectionString,
        string mutation)
    {
        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = mutation switch
        {
            "inbox-response-json" =>
                "UPDATE integration_inbox SET response_json = response_json || ' ';",
            "inbox-response-hash" =>
                "UPDATE integration_inbox SET response_sha256 = lower(hex(zeroblob(32)));",
            "outbox-correlation" =>
                "UPDATE integration_outbox SET correlation_id = 'different-request';",
            "outbox-payload" =>
                "UPDATE integration_outbox SET payload_json = payload_json || ' ';",
            "outbox-hash" =>
                "UPDATE integration_outbox SET content_sha256 = lower(hex(zeroblob(32)));",
            "outbox-missing" => "DELETE FROM integration_outbox;",
            _ => throw new ArgumentOutOfRangeException(
                nameof(mutation),
                mutation,
                "Unsupported Inbox evidence mutation.")
        };
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
