using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Tests;

public sealed class StationMaterialAndLeaseMessageTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 11, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-agent-material-lease",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MaterialArrivalSurvivesRestartAndPublishesCanonicalRabbitEvent()
    {
        Directory.CreateDirectory(_directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "agent.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            "material-arrival/plc/unit-001/scan-001",
            Guid.NewGuid(),
            "line.main",
            "station-system.main",
            StationMaterialArrivalSources.Plc,
            "plc.reader.main",
            Now);
        using (var store = new SqliteStationMaterialArrivalOutboxStore(connectionString))
        {
            var reporter = new StationMaterialArrivalReporter(
                "agent.main",
                "station.main",
                store);
            Assert.True(await reporter.ReportAsync(signal));
            Assert.False(await reporter.ReportAsync(signal));
        }

        var publisher = new CapturingPublisher();
        using (var restarted = new SqliteStationMaterialArrivalOutboxStore(connectionString))
        {
            var dispatcher = new StationMaterialArrivalOutboxDispatcher(restarted, publisher);
            Assert.Equal(1, await dispatcher.DispatchPendingAsync(10, Now.AddSeconds(1)));
            Assert.Empty(await restarted.ListPendingAsync(10));
        }

        Assert.Equal(StationAgentMessageKinds.MaterialArrived, publisher.Kind);
        var message = JsonSerializer.Deserialize<MaterialArrived>(publisher.Payload!);
        Assert.NotNull(message);
        Assert.Equal(signal.MessageId, message.MessageId);
        var publication = StationAgentEventPublicationFactory.Create(
            Options(),
            publisher.Kind!,
            publisher.Payload!);
        Assert.Equal(signal.MessageId, publication.MessageId);
        Assert.Equal(signal.ProductionUnitId, publication.CorrelationId);
        Assert.Equal("station.station.main.MaterialArrived", publication.RoutingKey);
    }

    [Fact]
    public async Task ResourceLeaseDeliveryPersistsFenceBeforeAckAndExactRedeliveryIsIdempotent()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "fences.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var change = LeaseChange(42);
        var processor = new StationJobDeliveryProcessor(Options());
        using var inbox = new SqliteStationResourceFenceValidator(
            connectionString,
            new FixedClock(Now));
        var coordinator = new StationResourceLeaseChangeCoordinator(
            "agent.main",
            "station.main",
            inbox);
        var first = new RecordingSettlement(failAcknowledgement: true);

        await Assert.ThrowsAsync<IOException>(async () =>
            await processor.ProcessAsync(
                Delivery(change, 1, redelivered: false),
                IgnoreJobAsync,
                coordinator.HandleAsync,
                first));

        var redelivery = new RecordingSettlement();
        await processor.ProcessAsync(
            Delivery(change, 2, redelivered: true),
            IgnoreJobAsync,
            coordinator.HandleAsync,
            redelivery);
        Assert.Equal([2UL], redelivery.Acknowledged);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM station_resource_lease_inbox),
                (SELECT fencing_token FROM station_resource_fences
                 WHERE resource_kind = 'Station' AND resource_id = 'station-system.main');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(42, reader.GetInt64(1));

        var stale = LeaseChange(41) with
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = "lease/job-main/station-system-main/41"
        };
        var rejected = new RecordingSettlement();
        await processor.ProcessAsync(
            Delivery(stale, 3, redelivered: false),
            IgnoreJobAsync,
            coordinator.HandleAsync,
            rejected);
        Assert.Equal([(3UL, false)], rejected.Rejected);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static RabbitMqStationTransportOptions Options() => new(
        new Uri("amqp://localhost"),
        "agent.main",
        "station.main",
        RequireTls: false);

    private static ResourceLeaseChanged LeaseChange(long token) => new(
        Guid.NewGuid(),
        $"lease/job-main/station-system-main/{token}",
        "agent.main",
        "station.main",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "operation.main@0001",
        "Station",
        "station-system.main",
        token,
        StationResourceLeaseStatuses.Granted,
        Now,
        Now.AddMinutes(5));

    private static StationTransportDelivery Delivery(
        ResourceLeaseChanged change,
        ulong deliveryTag,
        bool redelivered) => new(
        deliveryTag,
        "application/json",
        "utf-8",
        nameof(ResourceLeaseChanged),
        "coordinator.main",
        change.MessageId.ToString("D"),
        change.JobId.ToString("D"),
        $"station.{change.StationId}.resource-lease-changed",
        redelivered,
        JsonSerializer.SerializeToUtf8Bytes(change));

    private static ValueTask IgnoreJobAsync(
        StationJobRequested _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private sealed class CapturingPublisher : IStationAgentMessagePublisher
    {
        public string? Kind { get; private set; }
        public string? Payload { get; private set; }

        public ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Kind = kind;
            Payload = payloadJson;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSettlement(bool failAcknowledgement = false)
        : IStationTransportSettlement
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
                return ValueTask.FromException(new IOException("Ack connection closed."));
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

    private sealed class FixedClock(DateTimeOffset nowUtc) : IClock
    {
        public DateTimeOffset UtcNow => nowUtc;
    }
}
