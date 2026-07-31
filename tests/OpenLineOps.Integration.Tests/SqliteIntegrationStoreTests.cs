using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Application.Serialization;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.WorkOrders;
using OpenLineOps.Integration.Infrastructure.Persistence;

namespace OpenLineOps.Integration.Tests;

public sealed class SqliteIntegrationStoreTests
{
    [Fact]
    public async Task ConnectorContentConflictIsDeadLetteredWithoutPointlessRetries()
    {
        using var database = new TemporaryIntegrationDatabase();
        using var store = new SqliteIntegrationStore(database.ConnectionString);
        var request = IntegrationTestData.Request("request-connector-conflict");
        await new IdempotentWorkRequestService(
                store,
                new CountingWorkRequestHandler())
            .ProcessAsync(request, IntegrationTestData.Epoch.AddSeconds(2));
        var dispatchAt = IntegrationTestData.Epoch.AddMinutes(5);
        var dispatcher = new IntegrationOutboxDispatcher(
            store,
            new ContentConflictConnector(),
            new IntegrationOutboxDispatchOptions(maximumAttempts: 5));

        Assert.Equal(0, await dispatcher.DispatchAsync(1, dispatchAt));

        var snapshot = Assert.IsType<IntegrationOutboxSnapshot>(
            await store.GetOutboxAsync($"response-{request.Id.Value}"));
        Assert.Equal(1, snapshot.AttemptCount);
        Assert.Equal(dispatchAt, snapshot.DeadLetteredAtUtc);
        Assert.Contains(
            "different content",
            Assert.IsType<string>(snapshot.LastError));
        var failure = Assert.Single(
            await store.ListFailureAuditAsync($"response-{request.Id.Value}"));
        Assert.Equal(1, failure.AttemptCount);
        Assert.Equal(dispatchAt, failure.FailedAtUtc);
        Assert.True(failure.DeadLettered);
    }

    [Fact]
    public async Task LegacyPendingInboxSchemaIsUpgradedAndSafelyReclaimed()
    {
        using var database = new TemporaryIntegrationDatabase();
        var request = IntegrationTestData.Request("request-schema-upgrade");
        var requestJson = IntegrationMessageCodec.Encode(request);
        await using (var connection =
                     new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE integration_inbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT NOT NULL UNIQUE,
                    content_sha256 TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    status TEXT NOT NULL,
                    response_message_id TEXT NULL,
                    response_json TEXT NULL,
                    completed_at_utc TEXT NULL
                );

                INSERT INTO integration_inbox (
                    message_id, content_sha256, request_json, received_at_utc, status,
                    response_message_id, response_json, completed_at_utc)
                VALUES (
                    $message_id, $content_sha256, $request_json, $received_at_utc, 'Pending',
                    NULL, NULL, NULL);
                """;
            command.Parameters.AddWithValue("$message_id", request.Id.Value);
            command.Parameters.AddWithValue(
                "$content_sha256",
                IntegrationMessageCodec.ComputeSha256(requestJson));
            command.Parameters.AddWithValue("$request_json", requestJson);
            command.Parameters.AddWithValue(
                "$received_at_utc",
                IntegrationTestData.Epoch.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        using var upgradedStore =
            new SqliteIntegrationStore(database.ConnectionString);
        var handler = new CountingWorkRequestHandler();
        var result = await new IdempotentWorkRequestService(
                upgradedStore,
                handler,
                new IntegrationInboxProcessingOptions(TimeSpan.FromMinutes(1)))
            .ProcessAsync(
                request,
                IntegrationTestData.Epoch.AddMinutes(2));

        Assert.Equal(WorkRequestProcessingOutcome.Processed, result.Outcome);
        Assert.Equal(1, handler.InvocationCount);
        var claim = Assert.Single(
            await upgradedStore.ListClaimAuditAsync(request.Id.Value));
        Assert.True(claim.Reclaimed);
        Assert.Equal(request.SourceSystem, claim.ActorId);
    }

    [Fact]
    public async Task LegacyCompletedInboxResponseHashIsBackfilledOnlyFromConsistentOutboxEvidence()
    {
        using var database = new TemporaryIntegrationDatabase();
        var request = IntegrationTestData.Request("request-completed-schema-upgrade");
        var responseJson = await CreateLegacyCompletedInboxAsync(
            database.ConnectionString,
            request,
            outboxCorrelationMatches: true);

        var handler = new CountingWorkRequestHandler();
        using var upgradedStore = new SqliteIntegrationStore(database.ConnectionString);
        var replay = await new IdempotentWorkRequestService(upgradedStore, handler)
            .ProcessAsync(request, IntegrationTestData.Epoch.AddMinutes(2));

        Assert.Equal(WorkRequestProcessingOutcome.Replayed, replay.Outcome);
        Assert.Equal(0, handler.InvocationCount);
        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT response_sha256
            FROM integration_inbox
            WHERE message_id = $message_id;
            """;
        command.Parameters.AddWithValue("$message_id", request.Id.Value);
        Assert.Equal(
            IntegrationMessageCodec.ComputeSha256(responseJson),
            Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task LegacyCompletedInboxWithUntrustedOutboxEvidenceIsNotBackfilledOrReplayed()
    {
        using var database = new TemporaryIntegrationDatabase();
        var request = IntegrationTestData.Request("request-unsafe-schema-upgrade");
        await CreateLegacyCompletedInboxAsync(
            database.ConnectionString,
            request,
            outboxCorrelationMatches: false);

        var handler = new CountingWorkRequestHandler();
        using var upgradedStore = new SqliteIntegrationStore(database.ConnectionString);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await new IdempotentWorkRequestService(upgradedStore, handler)
                .ProcessAsync(request, IntegrationTestData.Epoch.AddMinutes(2)));
        Assert.Equal(0, handler.InvocationCount);

        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT response_sha256
            FROM integration_inbox
            WHERE message_id = $message_id;
            """;
        command.Parameters.AddWithValue("$message_id", request.Id.Value);
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DisposeClearsTheOwnedSqliteConnectionPool()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-integration-pool-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "integration.sqlite");
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true
        }.ToString();

        try
        {
            using (var store = new SqliteIntegrationStore(connectionString))
            {
                Assert.Empty(await store.ListAsync(new WorkOrderId("order-empty")));
            }

            Directory.Delete(directory, recursive: true);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

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

    private static async Task<string> CreateLegacyCompletedInboxAsync(
        string connectionString,
        OpenLineOps.Integration.Domain.Messages.WorkRequest request,
        bool outboxCorrelationMatches)
    {
        var requestJson = IntegrationMessageCodec.Encode(request);
        var response = IntegrationTestData.ResponseFor(request);
        var responseJson = IntegrationMessageCodec.Encode(response);
        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE integration_inbox (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id TEXT NOT NULL UNIQUE,
                content_sha256 TEXT NOT NULL,
                request_json TEXT NOT NULL,
                received_at_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                response_message_id TEXT NULL,
                response_json TEXT NULL,
                completed_at_utc TEXT NULL
            );

            CREATE TABLE integration_outbox (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id TEXT NOT NULL UNIQUE,
                correlation_id TEXT NOT NULL,
                content_sha256 TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                next_attempt_at_utc TEXT NOT NULL,
                last_error TEXT NULL,
                delivered_at_utc TEXT NULL,
                dead_lettered_at_utc TEXT NULL
            );

            INSERT INTO integration_inbox (
                message_id, content_sha256, request_json, received_at_utc, status,
                response_message_id, response_json, completed_at_utc)
            VALUES (
                $request_id, $request_sha256, $request_json, $received_at_utc, 'Completed',
                $response_id, $response_json, $completed_at_utc);

            INSERT INTO integration_outbox (
                message_id, correlation_id, content_sha256, payload_json,
                created_at_utc, attempt_count, next_attempt_at_utc,
                last_error, delivered_at_utc, dead_lettered_at_utc)
            VALUES (
                $response_id, $correlation_id, $response_sha256, $response_json,
                $completed_at_utc, 0, $completed_at_utc,
                NULL, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$request_id", request.Id.Value);
        command.Parameters.AddWithValue(
            "$request_sha256",
            IntegrationMessageCodec.ComputeSha256(requestJson));
        command.Parameters.AddWithValue("$request_json", requestJson);
        command.Parameters.AddWithValue(
            "$received_at_utc",
            IntegrationTestData.Epoch.ToString("O"));
        command.Parameters.AddWithValue("$response_id", response.Id.Value);
        command.Parameters.AddWithValue("$response_json", responseJson);
        command.Parameters.AddWithValue(
            "$response_sha256",
            IntegrationMessageCodec.ComputeSha256(responseJson));
        command.Parameters.AddWithValue(
            "$correlation_id",
            outboxCorrelationMatches ? request.Id.Value : "different-request");
        command.Parameters.AddWithValue(
            "$completed_at_utc",
            response.OccurredAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return responseJson;
    }

    private sealed class ContentConflictConnector : IIntegrationConnector
    {
        public ValueTask SendAsync(
            IntegrationOutboundMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException(
                new IntegrationConnectorMessageConflictException(
                    "Remote idempotency key already contains different content."));
        }
    }
}
