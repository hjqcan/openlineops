using System.Text;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Runtime.Tests;

public sealed class RabbitMqStationCoordinatorTransportReliabilityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConfirmFailureEscapesSoTransactionalOutboxCanRetrySameMessage()
    {
        var publisher = new FailOncePublicationTransport();
        var options = Options();
        var store = new InMemoryStationJobCoordinationStore();
        await using var transport = new RabbitMqStationCoordinatorTransport(
            options,
            store,
            publisher);
        var request = JobRequest();
        Assert.True(await EnqueueAsync(store, request));
        var lease = Assert.Single(await store.ListPendingAsync(10));
        Assert.Equal(nameof(ResourceLeaseChanged), lease.Kind);
        await store.MarkPublishedAsync(lease.MessageId);

        await Assert.ThrowsAsync<IOException>(async () =>
            await DispatchPendingAsync(store, transport));
        var failed = Assert.Single(await store.ListPendingAsync(10));
        Assert.Equal(1, failed.AttemptCount);

        await DispatchPendingAsync(store, transport);

        Assert.Equal(2, publisher.Attempts);
        Assert.Equal(
            publisher.Publications[0].MessageId,
            publisher.Publications[1].MessageId);
        Assert.Equal(
            publisher.Publications[0].CorrelationId,
            publisher.Publications[1].CorrelationId);
        Assert.True(publisher.Publications[0].Body.Span.SequenceEqual(
            publisher.Publications[1].Body.Span));
        var publication = publisher.Publications[1];
        Assert.Equal(request.MessageId, publication.MessageId);
        Assert.Equal(request.JobId, publication.CorrelationId);
        Assert.Equal($"station.{request.AgentId}.{request.StationId}", publication.RoutingKey);
        Assert.Empty(await store.ListPendingAsync(10));
    }

    [Fact]
    public async Task ResultInboxAckCrashRedeliveryPersistsCompletionExactlyOnce()
    {
        var store = new InMemoryStationJobCoordinationStore();
        var processor = new StationResultDeliveryProcessor(store);
        var request = JobRequest();
        Assert.True(await EnqueueAsync(store, request));
        var accepted = Accepted(request);
        await store.RecordAcceptedAsync(accepted);
        await store.RecordAcceptedAsync(accepted);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RecordAcceptedAsync(accepted with { MessageId = Guid.NewGuid() }));
        var completion = Completion(request);
        var first = new RecordingSettlement(failAcknowledgement: true);

        await Assert.ThrowsAsync<IOException>(async () =>
            await processor.ProcessAsync(
                ResultDelivery(completion, deliveryTag: 1, redelivered: false),
                first,
                IgnoreMaterialArrivalAsync,
                IgnoreRecoveryRequiredAsync));

        var redelivery = new RecordingSettlement();
        await processor.ProcessAsync(
            ResultDelivery(completion, deliveryTag: 2, redelivered: true),
            redelivery,
            IgnoreMaterialArrivalAsync,
            IgnoreRecoveryRequiredAsync);

        var persisted = await store.GetCompletionAsync(completion.IdempotencyKey);
        Assert.NotNull(persisted);
        Assert.Equal(completion.MessageId, persisted.MessageId);
        Assert.Equal(completion.JobId, persisted.JobId);
        Assert.Equal(completion.RuntimeSessionId, persisted.RuntimeSessionId);
        Assert.Equal(completion.ExecutionStatus, persisted.ExecutionStatus);
        Assert.Equal(completion.Judgement, persisted.Judgement);
        Assert.Equal([2UL], redelivery.Acknowledged);
        Assert.Empty(redelivery.Rejected);
    }

    [Fact]
    public async Task CompletionWithWrongEnvelopeIdentityIsRejectedBeforeInboxWrite()
    {
        var store = new InMemoryStationJobCoordinationStore();
        var request = JobRequest();
        Assert.True(await EnqueueAsync(store, request));
        await store.RecordAcceptedAsync(Accepted(request));
        var completion = Completion(request);
        var delivery = ResultDelivery(completion, 3, redelivered: false) with
        {
            AppId = "agent.other"
        };
        var settlement = new RecordingSettlement();

        await new StationResultDeliveryProcessor(store).ProcessAsync(
            delivery,
            settlement,
            IgnoreMaterialArrivalAsync,
            IgnoreRecoveryRequiredAsync);

        Assert.Null(await store.GetCompletionAsync(completion.IdempotencyKey));
        Assert.Equal([(3UL, false)], settlement.Rejected);
    }

    [Fact]
    public async Task DispatchOutboxPublishesCanonicalLeaseGrantBeforeStationJob()
    {
        var request = JobRequest() with
        {
            ResourceFences =
            [
                new StationResourceFence(
                    "Station",
                    "station-system.main",
                    7,
                    Now.AddMinutes(5))
            ]
        };
        var change = StationDispatchMessageIdentity.CreateLeaseGranted(
            request,
            Assert.Single(request.ResourceFences));
        var store = new InMemoryStationJobCoordinationStore();
        Assert.True(await store.TryEnqueueAsync(request, [change]));

        var leaseHead = Assert.Single(await store.ListPendingAsync(10));
        Assert.Equal(nameof(ResourceLeaseChanged), leaseHead.Kind);
        Assert.Equal(0, leaseHead.Sequence);
        await store.MarkPublishedAsync(leaseHead.MessageId);
        var jobHead = Assert.Single(await store.ListPendingAsync(10));
        Assert.Equal(nameof(StationJobRequested), jobHead.Kind);
        Assert.Equal(1, jobHead.Sequence);

        var publication = StationCoordinatorPublicationFactory.Create(Options(), change);
        Assert.Equal(request.JobId, publication.CorrelationId);
        Assert.Equal(
            "station.agent.main.station.main.resource-lease-changed",
            publication.RoutingKey);
    }

    [Fact]
    public async Task FailedFirstFenceBlocksEveryLaterFenceAndJobUntilHeadIsConfirmed()
    {
        var request = JobRequest() with
        {
            ResourceFences =
            [
                new StationResourceFence(
                    "Station",
                    "station-system.main",
                    7,
                    Now.AddMinutes(5)),
                new StationResourceFence(
                    "Device",
                    "device.vendor.main",
                    9,
                    Now.AddMinutes(5))
            ]
        };
        var store = new InMemoryStationJobCoordinationStore();
        Assert.True(await EnqueueAsync(store, request));
        var first = Assert.Single(await store.ListPendingAsync(10));
        Assert.Equal(0, first.Sequence);

        await store.RecordPublishFailureAsync(first.MessageId, "synthetic broker nack");

        var retry = Assert.Single(await store.ListPendingAsync(10));
        Assert.Equal(first.MessageId, retry.MessageId);
        Assert.Equal(nameof(ResourceLeaseChanged), retry.Kind);
        Assert.DoesNotContain(
            (await store.ListPendingAsync(10)),
            static item => item.Kind == nameof(StationJobRequested));
    }

    [Fact]
    public async Task QuarantineIsPermanentForEveryUnpublishedMessageInJobGroup()
    {
        var request = JobRequest();
        var store = new InMemoryStationJobCoordinationStore();
        Assert.True(await EnqueueAsync(store, request));

        await store.QuarantineJobAsync(
            request.JobId,
            "Production Run entered RecoveryRequired before dispatch.",
            Now.AddSeconds(1));
        await store.QuarantineJobAsync(
            request.JobId,
            "Production Run entered RecoveryRequired before dispatch.",
            Now.AddSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.QuarantineJobAsync(
                request.JobId,
                "Conflicting quarantine evidence.",
                Now.AddSeconds(3)));

        Assert.Empty(await store.ListPendingAsync(10));
        var quarantined = (await store.ListQuarantinedAsync(request.JobId)).ToArray();
        Assert.Equal(2, quarantined.Length);
        Assert.Equal([0, 1], quarantined.Select(static item => item.Sequence).ToArray());
        Assert.All(quarantined, item => Assert.Equal(
            "Production Run entered RecoveryRequired before dispatch.",
            item.Reason));
        Assert.All(quarantined, item => Assert.Equal(Now.AddSeconds(1), item.QuarantinedAtUtc));
        var exception = await Assert.ThrowsAsync<StationJobDispatchQuarantinedException>(async () =>
            await new DurableStationJobGateway(store)
                .DispatchAsync(request)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(exception.NeverPublished);
    }

    [Fact]
    public async Task ResultInboxRejectsUnknownOutOfOrderSpoofedAndNullNestedEvidence()
    {
        var store = new InMemoryStationJobCoordinationStore();
        var request = JobRequest();
        var completion = Completion(request);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RecordCompletionAsync(completion));

        Assert.True(await EnqueueAsync(store, request));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RecordCompletionAsync(completion));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RecordAcceptedAsync(Accepted(request) with { AgentId = "agent.spoof" }));

        await store.RecordAcceptedAsync(Accepted(request));
        var malformed = completion with
        {
            MessageId = Guid.NewGuid(),
            Steps = [null!]
        };
        var settlement = new RecordingSettlement();
        await new StationResultDeliveryProcessor(store).ProcessAsync(
            ResultDelivery(malformed, 30, redelivered: false),
            settlement,
            IgnoreMaterialArrivalAsync,
            IgnoreRecoveryRequiredAsync);

        Assert.Equal([(30UL, false)], settlement.Rejected);
        Assert.Null(await store.GetCompletionAsync(request.IdempotencyKey));

        var invalidOutcomes = new[]
        {
            completion with
            {
                MessageId = Guid.NewGuid(),
                Judgement = OpenLineOps.Runtime.Contracts.ResultJudgement.Unknown
            },
            completion with
            {
                MessageId = Guid.NewGuid(),
                ExecutionStatus = OpenLineOps.Runtime.Contracts.ExecutionStatus.Failed,
                Judgement = OpenLineOps.Runtime.Contracts.ResultJudgement.Passed,
                FailureCode = "Agent.ExecutionFailed",
                FailureReason = "Synthetic failure"
            },
            completion with
            {
                MessageId = Guid.NewGuid(),
                ExecutionStatus = OpenLineOps.Runtime.Contracts.ExecutionStatus.Canceled,
                Judgement = OpenLineOps.Runtime.Contracts.ResultJudgement.Aborted,
                FailureCode = "Agent.ExecutionCanceled",
                FailureReason = null
            }
        };
        for (var index = 0; index < invalidOutcomes.Length; index++)
        {
            var invalidSettlement = new RecordingSettlement();
            await new StationResultDeliveryProcessor(store).ProcessAsync(
                ResultDelivery(
                    invalidOutcomes[index],
                    checked((ulong)(31 + index)),
                    redelivered: false),
                invalidSettlement,
                IgnoreMaterialArrivalAsync,
                IgnoreRecoveryRequiredAsync);
            Assert.Equal(
                [(checked((ulong)(31 + index)), false)],
                invalidSettlement.Rejected);
        }

        Assert.Null(await store.GetCompletionAsync(request.IdempotencyKey));
    }

    [Fact]
    public async Task DurableGatewayStopsPollingWhenAgentReportsRecoveryRequired()
    {
        var store = new InMemoryStationJobCoordinationStore();
        var request = JobRequest();
        Assert.True(await EnqueueAsync(store, request));
        await store.RecordAcceptedAsync(Accepted(request));
        var evidence = RecoveryRequired(request);
        await store.RecordRecoveryRequiredAsync(evidence);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RecordCompletionAsync(Completion(request)));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RecordProgressAsync(new StationJobProgressed(
                Guid.NewGuid(),
                request.JobId,
                request.IdempotencyKey,
                request.AgentId,
                request.StationId,
                75,
                "Late progress",
                Now.AddSeconds(3))));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RecordRecoveryRequiredAsync(evidence with
            {
                MessageId = Guid.NewGuid(),
                IdempotencyKey = $"{request.IdempotencyKey}/recovery-required/0002"
            }));
        var gateway = new DurableStationJobGateway(store);

        var exception = await Assert.ThrowsAsync<StationJobRecoveryRequiredException>(async () =>
            await gateway.DispatchAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(evidence, exception.Evidence);
    }

    [Fact]
    public async Task MaterialArrivalAckCrashRedeliveryUsesDurableHandlerIdempotency()
    {
        var store = new InMemoryStationJobCoordinationStore();
        var processor = new StationResultDeliveryProcessor(store);
        var message = new MaterialArrived(
            Guid.NewGuid(),
            "material-arrival/plc/unit-001/scan-001",
            "agent.main",
            "station.main",
            "project.main",
            "application.main",
            "snapshot.main",
            new string('a', 64),
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            "line.main",
            "station-system.main",
            StationMaterialArrivalSources.Plc,
            "plc.reader.main",
            Now);
        var durableInbox = new HashSet<Guid>();
        var applications = 0;
        ValueTask HandleAsync(MaterialArrived arrival, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (durableInbox.Add(arrival.MessageId))
            {
                applications++;
            }

            return ValueTask.CompletedTask;
        }

        var first = new RecordingSettlement(failAcknowledgement: true);
        await Assert.ThrowsAsync<IOException>(async () =>
            await processor.ProcessAsync(
                MaterialDelivery(message, 20, redelivered: false),
                first,
                HandleAsync,
                IgnoreRecoveryRequiredAsync));
        var redelivery = new RecordingSettlement();
        await processor.ProcessAsync(
            MaterialDelivery(message, 21, redelivered: true),
            redelivery,
            HandleAsync,
            IgnoreRecoveryRequiredAsync);

        Assert.Equal(1, applications);
        Assert.Equal([21UL], redelivery.Acknowledged);
    }

    [Fact]
    public async Task MaterialArrivalRejectsUppercaseUuidHashAndUnknownJsonPermanently()
    {
        var processor = new StationResultDeliveryProcessor(
            new InMemoryStationJobCoordinationStore());
        var message = new MaterialArrived(
            Guid.NewGuid(),
            $"material-arrival/{Guid.NewGuid():D}",
            "agent.main",
            "station.main",
            "project.main",
            "application.main",
            "snapshot.main",
            new string('a', 64),
            StationMaterialKinds.Carrier,
            "carrier-main",
            "line.main",
            "station-system.main",
            StationMaterialArrivalSources.Plc,
            "plc.reader.main",
            Now);
        var canonicalJson = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions()));
        var uppercaseUuidJson = canonicalJson.Replace(
            message.MessageId.ToString("D"),
            message.MessageId.ToString("D").ToUpperInvariant(),
            StringComparison.Ordinal);
        var unknownFieldJson = $"{canonicalJson[..^1]},\"unknownToken\":true}}";
        var uppercaseHashJson = canonicalJson.Replace(
            new string('a', 64),
            new string('A', 64),
            StringComparison.Ordinal);
        var handled = 0;
        ValueTask HandleAsync(MaterialArrived _, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            handled++;
            return ValueTask.CompletedTask;
        }

        foreach (var (deliveryTag, json) in new[]
                 {
                     (31UL, uppercaseUuidJson),
                     (32UL, uppercaseHashJson),
                     (33UL, unknownFieldJson)
                 })
        {
            var settlement = new RecordingSettlement();
            await processor.ProcessAsync(
                MaterialDelivery(message, deliveryTag, redelivered: false) with
                {
                    Body = Encoding.UTF8.GetBytes(json)
                },
                settlement,
                HandleAsync,
                IgnoreRecoveryRequiredAsync);
            Assert.Equal([(deliveryTag, false)], settlement.Rejected);
        }

        Assert.Equal(0, handled);
    }

    [Fact]
    public void SafetyCorrelatorRejectsAcknowledgementWithWrongPendingIdentity()
    {
        var request = SafeStopRequest();
        var acknowledgement = new StationSafeStopAcknowledged(
            Guid.NewGuid(),
            request.MessageId,
            "safe-stop/different-evidence",
            request.AgentId,
            request.StationId,
            true,
            null,
            null,
            Now.AddSeconds(1));
        var correlator = new StationSafetyAcknowledgementCorrelator();
        using var registration = correlator.Register(request);

        Assert.Throws<InvalidDataException>(() => correlator.Accept(
            AcknowledgementDelivery(
                acknowledgement,
                nameof(StationSafeStopAcknowledged),
                "safe-stop-acknowledged")));
        Assert.False(registration.Task.IsCompleted);
    }

    [Fact]
    public async Task EmergencyAcknowledgementUsesExactEnvelopeAndPendingCorrelation()
    {
        var request = new EmergencyStopRequested(
            Guid.NewGuid(),
            "emergency/station.main/guard-open",
            "agent.main",
            "station.main",
            "Guard opened",
            "operator.main",
            Now);
        var acknowledgement = new EmergencyStopAcknowledged(
            Guid.NewGuid(),
            request.MessageId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            true,
            null,
            null,
            Now.AddSeconds(1));
        var correlator = new StationSafetyAcknowledgementCorrelator();
        using var registration = correlator.Register(request);

        correlator.Accept(AcknowledgementDelivery(
            acknowledgement,
            nameof(EmergencyStopAcknowledged),
            "emergency-stop-acknowledged"));

        Assert.Equal(acknowledgement, await registration.Task);
    }

    [Fact]
    public void EmergencyPublicationHasDedicatedHighestPriorityAndRequestCorrelation()
    {
        var request = new EmergencyStopRequested(
            Guid.NewGuid(),
            "emergency/station.main/guard-open",
            "agent.main",
            "station.main",
            "Guard opened",
            "operator.main",
            Now);

        var publication = StationCoordinatorPublicationFactory.Create(Options(), request);

        Assert.Equal((byte)10, publication.Priority);
        Assert.Equal(request.MessageId, publication.MessageId);
        Assert.Equal(request.MessageId, publication.CorrelationId);
        Assert.Equal(
            "station.agent.main.station.main.emergency-stop",
            publication.RoutingKey);
    }

    private static StationCoordinatorTransportOptions Options() => new()
    {
        BrokerUri = "amqp://localhost",
        RequireTls = false,
        CoordinatorId = "coordinator.main"
    };

    private static async Task DispatchPendingAsync(
        InMemoryStationJobCoordinationStore store,
        RabbitMqStationCoordinatorTransport transport)
    {
        var item = Assert.Single(await store.ListPendingAsync(10));
        var request = JsonSerializer.Deserialize<StationJobRequested>(
            item.PayloadJson,
            JsonOptions())
            ?? throw new InvalidDataException("Station job Outbox payload is null.");
        try
        {
            await transport.PublishAsync(request);
            await store.MarkPublishedAsync(item.MessageId);
        }
        catch (Exception exception)
        {
            await store.RecordPublishFailureAsync(
                item.MessageId,
                exception.Message);
            throw;
        }
    }

    private static StationJobRequested JobRequest()
    {
        using var inputs = JsonDocument.Parse("{}");
        return new StationJobRequested(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "job/unit-001/operation.main/1",
            "agent.main",
            "station.main",
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
                7,
                Now.AddMinutes(5))],
            inputs.RootElement.Clone(),
            Now);
    }

    private static StationJobAccepted Accepted(StationJobRequested request) => new(
        Guid.NewGuid(),
        request.JobId,
        request.IdempotencyKey,
        request.AgentId,
        request.StationId,
        Now.AddSeconds(1));

    private static StationJobRecoveryRequired RecoveryRequired(StationJobRequested request) => new(
        Guid.NewGuid(),
        $"{request.IdempotencyKey}/recovery-required/0001",
        request.JobId,
        request.IdempotencyKey,
        request.AgentId,
        request.StationId,
        request.ProductionRunId,
        request.OperationRunId,
        request.RuntimeSessionId,
        "Agent restarted during non-idempotent execution.",
        Now.AddSeconds(2));

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
            Now.AddSeconds(2));
    }

    private static async ValueTask<bool> EnqueueAsync(
        InMemoryStationJobCoordinationStore store,
        StationJobRequested request)
    {
        var changes = request.ResourceFences
            .Select(fence => StationDispatchMessageIdentity.CreateLeaseGranted(request, fence))
            .ToArray();
        return await store.TryEnqueueAsync(request, changes);
    }

    private static StationSafeStopRequested SafeStopRequest() => new(
        Guid.NewGuid(),
        "safe-stop/run.main/station.main",
        "agent.main",
        "station.main",
        "station-system.main",
        Guid.NewGuid(),
        "operation.main@0001",
        "operator.main",
        "Operator requested safe stop",
        Now);

    private static StationCoordinatorTransportDelivery ResultDelivery(
        StationJobCompleted completion,
        ulong deliveryTag,
        bool redelivered) => new(
        deliveryTag,
        "application/json",
        "utf-8",
        nameof(StationJobCompleted),
        completion.AgentId,
        completion.MessageId.ToString("D"),
        completion.JobId.ToString("D"),
        $"station.{completion.StationId}.{nameof(StationJobCompleted)}",
        redelivered,
        JsonSerializer.SerializeToUtf8Bytes(completion, JsonOptions()));

    private static StationCoordinatorTransportDelivery AcknowledgementDelivery<T>(
        T acknowledgement,
        string type,
        string routeSuffix)
    {
        var (messageId, requestId, agentId, stationId) = acknowledgement switch
        {
            EmergencyStopAcknowledged value => (
                value.MessageId,
                value.RequestMessageId,
                value.AgentId,
                value.StationId),
            StationSafeStopAcknowledged value => (
                value.MessageId,
                value.RequestMessageId,
                value.AgentId,
                value.StationId),
            StationJobCancelAcknowledged value => (
                value.MessageId,
                value.RequestMessageId,
                value.AgentId,
                value.StationId),
            _ => throw new ArgumentOutOfRangeException(nameof(acknowledgement))
        };
        return new StationCoordinatorTransportDelivery(
            1,
            "application/json",
            "utf-8",
            type,
            agentId,
            messageId.ToString("D"),
            requestId.ToString("D"),
            $"station.{stationId}.{routeSuffix}",
            false,
            JsonSerializer.SerializeToUtf8Bytes(acknowledgement, JsonOptions()));
    }

    private static StationCoordinatorTransportDelivery MaterialDelivery(
        MaterialArrived message,
        ulong deliveryTag,
        bool redelivered) => new(
        deliveryTag,
        "application/json",
        "utf-8",
        nameof(MaterialArrived),
        message.ProducerId,
        message.MessageId.ToString("D"),
        message.MessageId.ToString("D"),
        $"station.{message.StationId}.{nameof(MaterialArrived)}",
        redelivered,
        JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions()));

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private static ValueTask IgnoreMaterialArrivalAsync(
        MaterialArrived _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static ValueTask IgnoreRecoveryRequiredAsync(
        StationJobRecoveryRequired _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private sealed class FailOncePublicationTransport :
        IStationCoordinatorConfirmedPublicationTransport
    {
        public int Attempts { get; private set; }

        public List<StationCoordinatorPublication> Publications { get; } = [];

        public ValueTask PublishAsync(
            StationCoordinatorPublication publication,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            Publications.Add(publication);
            return Attempts == 1
                ? ValueTask.FromException(new IOException("Broker publish was nacked."))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSettlement(bool failAcknowledgement = false)
        : IStationCoordinatorTransportSettlement
    {
        public List<ulong> Acknowledged { get; } = [];

        public List<(ulong DeliveryTag, bool Requeue)> Rejected { get; } = [];

        public ValueTask AcknowledgeAsync(
            ulong deliveryTag,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failAcknowledgement)
            {
                return ValueTask.FromException(new IOException("Ack connection was closed."));
            }

            Acknowledged.Add(deliveryTag);
            return ValueTask.CompletedTask;
        }

        public ValueTask RejectAsync(
            ulong deliveryTag,
            bool requeue,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rejected.Add((deliveryTag, requeue));
            return ValueTask.CompletedTask;
        }
    }
}
