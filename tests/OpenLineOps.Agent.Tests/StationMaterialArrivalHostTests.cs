using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Agent;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Packages;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Infrastructure.Releases;

namespace OpenLineOps.Agent.Tests;

public sealed class StationMaterialArrivalHostTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 14, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-agent-material-host-{Guid.NewGuid():N}");

    [Fact]
    public async Task CurrentUserPipeAndHostedOutboxSurviveColdRestartWithoutReordering()
    {
        Directory.CreateDirectory(_root);
        var package = await BuildSignedPackageAsync();
        var databasePath = Path.Combine(_root, "station-agent.sqlite");
        var pipeName = $"openlineops-material-{Guid.NewGuid():N}";
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            $"material-arrival/plc/{Guid.NewGuid():D}",
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.main",
            Now);

        var offlinePublisher = new HostPublisher(fail: true);
        using (var host = CreateHost(
                         databasePath,
                         pipeName,
                         package,
                         new MutableClock(Now.AddSeconds(1)),
                         offlinePublisher))
        {
            await host.StartAsync();
            await using (var partialFrame = new NamedPipeClientStream(
                             ".",
                             pipeName,
                             PipeDirection.InOut,
                             PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await partialFrame.ConnectAsync(5000);
                await partialFrame.WriteAsync(new byte[] { 1, 0 });
                await partialFrame.FlushAsync();
                await Task.Delay(400);
            }

            var response = await new StationMaterialArrivalLocalIpcClient(
                    new StationMaterialArrivalLocalIpcOptions(pipeName))
                .ReportAsync(signal, TimeSpan.FromSeconds(5));
            Assert.True(response.Accepted);
            Assert.False(response.Replayed);
            Assert.Equal(signal.MessageId, response.MessageId);
            await WaitUntilAsync(() => offlinePublisher.AttemptCount == 1);
            await host.StopAsync();
        }

        var onlinePublisher = new HostPublisher(fail: false);
        using (var restarted = CreateHost(
                         databasePath,
                         pipeName,
                         package,
                         new MutableClock(Now.AddSeconds(2)),
                         onlinePublisher))
        {
            await restarted.StartAsync();
            await WaitUntilAsync(() => onlinePublisher.PublishedMessageIds.Length == 1);
            Assert.Equal([signal.MessageId], onlinePublisher.PublishedMessageIds);
            var replay = await new StationMaterialArrivalLocalIpcClient(
                    new StationMaterialArrivalLocalIpcOptions(pipeName))
                .ReportAsync(signal, TimeSpan.FromSeconds(5));
            Assert.True(replay.Accepted);
            Assert.True(replay.Replayed);
            await Task.Delay(400);
            Assert.Equal(1, onlinePublisher.AttemptCount);
            await restarted.StopAsync();
        }

        await using var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_count, published_at_utc, quarantined_at_utc
            FROM station_material_arrival_outbox
            WHERE message_id = $message_id;
            """;
        command.Parameters.AddWithValue("$message_id", signal.MessageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public async Task ClientRetriesTheSameMessageAfterAcknowledgementConnectionDrops()
    {
        var pipeName = $"openlineops-material-retry-{Guid.NewGuid():N}";
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            $"material-arrival/plc/{Guid.NewGuid():D}",
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.retry",
            Now);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            byte[] firstPayload;
            await using (var first = CreateTestPipe(pipeName))
            {
                await first.WaitForConnectionAsync(timeout.Token);
                firstPayload = await StationMaterialArrivalLocalIpcServer.ReadFrameAsync(
                    first,
                    64 * 1024,
                    timeout.Token);
            }

            await using var second = CreateTestPipe(pipeName);
            await second.WaitForConnectionAsync(timeout.Token);
            var replayPayload = await StationMaterialArrivalLocalIpcServer.ReadFrameAsync(
                second,
                64 * 1024,
                timeout.Token);
            Assert.Equal(firstPayload, replayPayload);
            await StationMaterialArrivalLocalIpcServer.WriteFrameAsync(
                second,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        messageId = signal.MessageId,
                        accepted = true,
                        replayed = true,
                        failureCode = (string?)null
                    }),
                timeout.Token);
        }, timeout.Token);

        var response = await new StationMaterialArrivalLocalIpcClient(
                new StationMaterialArrivalLocalIpcOptions(pipeName))
            .ReportAsync(signal, TimeSpan.FromSeconds(5), timeout.Token);
        await server;

        Assert.True(response.Accepted);
        Assert.True(response.Replayed);
        Assert.Equal(signal.MessageId, response.MessageId);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static NamedPipeServerStream CreateTestPipe(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private IHost CreateHost(
        string databasePath,
        string pipeName,
        PackageFixture package,
        MutableClock clock,
        HostPublisher publisher)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<IClock>(serviceProvider =>
            serviceProvider.GetRequiredService<MutableClock>());
        builder.Services.AddSingleton(_ =>
            new SqliteStationMaterialArrivalOutboxStore(ConnectionString(databasePath)));
        builder.Services.AddSingleton<IStationMaterialArrivalOutboxStore>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteStationMaterialArrivalOutboxStore>());
        builder.Services.AddSingleton(_ => new SignedStationPackageInstaller(
            new StationPackageTrustOptions(
                Path.Combine(_root, "cache"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["host-test-signing"] = package.PublicKeyPem
                })));
        builder.Services.AddSingleton<IStationMaterialArrivalDeploymentProvider>(serviceProvider =>
            new SignedStationMaterialArrivalDeploymentProvider(
                new SignedStationMaterialArrivalDeploymentOptions(
                    "agent.main",
                    "station.main",
                    package.PackagePath,
                    package.ContentSha256),
                serviceProvider.GetRequiredService<SignedStationPackageInstaller>()));
        builder.Services.AddSingleton<StationMaterialArrivalReporter>();
        builder.Services.AddSingleton<IStationAgentMessagePublisher>(publisher);
        builder.Services.AddSingleton<StationMaterialArrivalOutboxDispatcher>();
        builder.Services.AddSingleton(new StationMaterialArrivalLocalIpcOptions(
            pipeName,
            ConnectionTimeout: TimeSpan.FromMilliseconds(200)));
        builder.Services.AddSingleton<StationMaterialArrivalLocalIpcServer>();
        builder.Services.AddHostedService<StationMaterialArrivalWorker>();
        return builder.Build();
    }

    private async ValueTask<PackageFixture> BuildSignedPackageAsync()
    {
        var source = Path.Combine(_root, "source");
        var distribution = Path.Combine(_root, "distribution");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(distribution);
        await File.WriteAllTextAsync(Path.Combine(source, "release.json"), "{}");
        using var rsa = RSA.Create(3072);
        var built = await SignedStationPackageBuilder.BuildAsync(
            new BuildStationPackageRequest(
                source,
                Path.Combine(distribution, "station.olopkg"),
                "package.host-test",
                "project.main",
                "application.main",
                "snapshot.main",
                "line.main",
                "station-system.main",
                "host-test-signing",
                rsa.ExportRSAPrivateKeyPem(),
                Now));
        return new PackageFixture(
            built.PackagePath,
            built.Manifest.ContentSha256,
            rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static string ConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= timeoutAtUtc)
            {
                throw new TimeoutException("Station material arrival Host condition was not reached.");
            }

            await Task.Delay(25);
        }
    }

    private sealed record PackageFixture(
        string PackagePath,
        string ContentSha256,
        string PublicKeyPem);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class HostPublisher(bool fail) : IStationAgentMessagePublisher
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);
        private readonly object _gate = new();
        private readonly List<Guid> _publishedMessageIds = [];
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public Guid[] PublishedMessageIds
        {
            get
            {
                lock (_gate)
                {
                    return _publishedMessageIds.ToArray();
                }
            }
        }

        public ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    kind,
                    StationAgentMessageKinds.MaterialArrived,
                    StringComparison.Ordinal))
            {
                return ValueTask.FromException(new InvalidDataException(
                    $"Unexpected Station message kind '{kind}'."));
            }

            var message = JsonSerializer.Deserialize<MaterialArrived>(payloadJson, JsonOptions)
                ?? throw new InvalidDataException("Material arrival Host payload is null.");
            Interlocked.Increment(ref _attemptCount);
            if (fail)
            {
                return ValueTask.FromException(new IOException("RabbitMQ is offline."));
            }

            lock (_gate)
            {
                _publishedMessageIds.Add(message.MessageId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
