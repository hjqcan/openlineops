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
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
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
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.main",
            Now);
        using (var store = new SqliteStationMaterialArrivalOutboxStore(connectionString))
        {
            var reporter = new StationMaterialArrivalReporter(
                new FixedMaterialArrivalDeploymentProvider(),
                store,
                new FixedClock(Now.AddSeconds(1)));
            Assert.True(await reporter.ReportAsync(signal));
            Assert.False(await reporter.ReportAsync(signal));
        }

        var publisher = new CapturingPublisher();
        using (var restarted = new SqliteStationMaterialArrivalOutboxStore(connectionString))
        {
            var dispatcher = new StationMaterialArrivalOutboxDispatcher(restarted, publisher);
            Assert.Equal(1, await dispatcher.DispatchPendingAsync(10, Now.AddSeconds(1)));
            Assert.Empty(await restarted.ListPendingAsync(10, Now.AddSeconds(1)));
        }

        Assert.Equal(StationAgentMessageKinds.MaterialArrived, publisher.Kind);
        var message = JsonSerializer.Deserialize<MaterialArrived>(
            publisher.Payload!,
            JsonOptions);
        Assert.NotNull(message);
        Assert.Equal(signal.MessageId, message.MessageId);
        Assert.Equal("project.main", message.ProjectId);
        Assert.Equal("application.main", message.ApplicationId);
        Assert.Equal("snapshot.main", message.ProjectSnapshotId);
        Assert.Equal("line.main", message.LineId);
        Assert.Equal("station-system.main", message.StationSystemId);
        Assert.Equal(new string('a', 64), message.PackageContentSha256);
        var publication = StationAgentEventPublicationFactory.Create(
            Options(),
            publisher.Kind!,
            publisher.Payload!);
        Assert.Equal(signal.MessageId, publication.MessageId);
        Assert.Equal(signal.MessageId, publication.CorrelationId);
        Assert.Equal(
            StationTransportRoute.Event(
                "agent.main",
                "station.main",
                nameof(MaterialArrived)),
            publication.RoutingKey);
    }

    [Fact]
    public void MaterialSignalCannotSelectAnyDeploymentIdentity()
    {
        Assert.Equal(
            [
                nameof(StationMaterialArrivalSignal.MessageId),
                nameof(StationMaterialArrivalSignal.IdempotencyKey),
                nameof(StationMaterialArrivalSignal.MaterialKind),
                nameof(StationMaterialArrivalSignal.MaterialId),
                nameof(StationMaterialArrivalSignal.Source),
                nameof(StationMaterialArrivalSignal.ActorId),
                nameof(StationMaterialArrivalSignal.ArrivedAtUtc)
            ],
            typeof(StationMaterialArrivalSignal)
                .GetProperties()
                .Select(static property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task MaterialOutboxUsesDurableBackoffAndGlobalHeadOfLineOrdering()
    {
        Directory.CreateDirectory(_directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "agent-hol.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var first = Signal("unit-hol-001", Now);
        var second = Signal("unit-hol-002", Now.AddMinutes(-10));
        using (var store = new SqliteStationMaterialArrivalOutboxStore(connectionString))
        {
            var reporter = new StationMaterialArrivalReporter(
                new FixedMaterialArrivalDeploymentProvider(),
                store,
                new FixedClock(Now.AddSeconds(1)));
            Assert.True(await reporter.ReportAsync(first));
            Assert.True(await reporter.ReportAsync(second));
            var failing = new OrderedPublisher(failuresRemaining: 1);
            var dispatcher = new StationMaterialArrivalOutboxDispatcher(store, failing);
            Assert.Equal(0, await dispatcher.DispatchPendingAsync(10, Now.AddSeconds(1)));
            Assert.Equal([first.MessageId], failing.AttemptedMessageIds);
            Assert.Empty(await store.ListPendingAsync(10, Now.AddSeconds(1)));
        }

        using (var restarted = new SqliteStationMaterialArrivalOutboxStore(connectionString))
        {
            var publisher = new OrderedPublisher();
            var dispatcher = new StationMaterialArrivalOutboxDispatcher(restarted, publisher);
            Assert.Equal(0, await dispatcher.DispatchPendingAsync(
                10,
                Now.AddSeconds(1).AddMilliseconds(249)));
            Assert.Empty(publisher.AttemptedMessageIds);
            Assert.Equal(2, await dispatcher.DispatchPendingAsync(
                10,
                Now.AddSeconds(1).AddMilliseconds(250)));
            Assert.Equal([first.MessageId, second.MessageId], publisher.AttemptedMessageIds);
        }
    }

    [Fact]
    public async Task PoisonedMaterialHeadIsQuarantinedOnceBeforeNextMessagePublishes()
    {
        Directory.CreateDirectory(_directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "agent-poison.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var first = Signal("unit-poison-001", Now);
        var second = Signal("unit-poison-002", Now.AddMilliseconds(1));
        using var store = new SqliteStationMaterialArrivalOutboxStore(connectionString);
        var reporter = new StationMaterialArrivalReporter(
            new FixedMaterialArrivalDeploymentProvider(),
            store,
            new FixedClock(Now.AddSeconds(1)));
        Assert.True(await reporter.ReportAsync(first));
        Assert.True(await reporter.ReportAsync(second));
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var poison = connection.CreateCommand();
            poison.CommandText = """
                UPDATE station_material_arrival_outbox
                SET payload_json = '{}'
                WHERE message_id = $message_id;
                """;
            poison.Parameters.AddWithValue("$message_id", first.MessageId.ToString("D"));
            Assert.Equal(1, await poison.ExecuteNonQueryAsync());
        }

        var publisher = new OrderedPublisher();
        var dispatcher = new StationMaterialArrivalOutboxDispatcher(store, publisher);
        Assert.Equal(1, await dispatcher.DispatchPendingAsync(10, Now.AddSeconds(1)));
        Assert.Equal([second.MessageId], publisher.AttemptedMessageIds);
        Assert.Empty(await store.ListPendingAsync(10, Now.AddSeconds(2)));
        await using var verification = new SqliteConnection(connectionString);
        await verification.OpenAsync();
        await using var query = verification.CreateCommand();
        query.CommandText = """
            SELECT attempt_count, quarantined_at_utc
            FROM station_material_arrival_outbox
            WHERE message_id = $message_id;
            """;
        query.Parameters.AddWithValue("$message_id", first.MessageId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(reader.IsDBNull(1));
    }

    [Fact]
    public async Task MaterialReporterRejectsTimestampBeyondFutureClockSkewBeforeEnqueue()
    {
        var store = new InMemoryStationMaterialArrivalOutboxStore();
        var reporter = new StationMaterialArrivalReporter(
            new FixedMaterialArrivalDeploymentProvider(),
            store,
            new FixedClock(Now));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reporter.ReportAsync(Signal(
                "unit-future-001",
                Now.AddMinutes(5).AddMilliseconds(1))));
        Assert.Empty(await store.ListPendingAsync(10, Now));
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

        var spoofedTarget = LeaseChange(43) with
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = "lease/job-main/station-system-spoof/43",
            StationSystemId = "station-system.spoof"
        };
        var spoofedSettlement = new RecordingSettlement();
        await processor.ProcessAsync(
            Delivery(spoofedTarget, 4, redelivered: false),
            IgnoreJobAsync,
            coordinator.HandleAsync,
            spoofedSettlement);
        Assert.Equal([(4UL, false)], spoofedSettlement.Rejected);
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
        "station-system.main",
        RequireTls: false);

    private static ResourceLeaseChanged LeaseChange(long token) => new(
        Guid.NewGuid(),
        $"lease/job-main/station-system-main/{token}",
        "agent.main",
        "station.main",
        "station-system.main",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "operation.main@0001",
        "Station",
        "station-system.main",
        token,
        StationResourceLeaseStatuses.Granted,
        Now,
        Now.AddMinutes(5));

    private static StationMaterialArrivalSignal Signal(
        string materialIdentity,
        DateTimeOffset arrivedAtUtc) => new(
        Guid.NewGuid(),
        $"material-arrival/plc/{materialIdentity}/{Guid.NewGuid():D}",
        StationMaterialKinds.ProductionUnit,
        Guid.NewGuid().ToString("D"),
        StationMaterialArrivalSources.Plc,
        "plc.reader.main",
        arrivedAtUtc);

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
        StationTransportRoute.ResourceLeaseChanged(change.AgentId, change.StationId),
        redelivered,
        JsonSerializer.SerializeToUtf8Bytes(change, JsonOptions));

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

    private sealed class OrderedPublisher(int failuresRemaining = 0) :
        IStationAgentMessagePublisher
    {
        private int _failuresRemaining = failuresRemaining;

        public List<Guid> AttemptedMessageIds { get; } = [];

        public ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(StationAgentMessageKinds.MaterialArrived, kind);
            var message = JsonSerializer.Deserialize<MaterialArrived>(payloadJson, JsonOptions)
                ?? throw new InvalidDataException("Material arrival payload is null.");
            AttemptedMessageIds.Add(message.MessageId);
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                return ValueTask.FromException(new IOException("Broker is offline."));
            }

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

    private sealed class FixedMaterialArrivalDeploymentProvider :
        IStationMaterialArrivalDeploymentProvider
    {
        public ValueTask<VerifiedStationMaterialArrivalDeployment> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new VerifiedStationMaterialArrivalDeployment(
                "agent.main",
                "station.main",
                "project.main",
                "application.main",
                "snapshot.main",
                "line.main",
                "station-system.main",
                new string('a', 64)));
        }
    }
}
