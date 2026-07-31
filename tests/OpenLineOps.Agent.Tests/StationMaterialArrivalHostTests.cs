using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
using OpenLineOps.ContentProtection;
using OpenLineOps.Projects.Infrastructure.Releases;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class StationMaterialArrivalHostTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 14, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-agent-material-host-{Guid.NewGuid():N}");

    [Fact]
    public async Task IdentityBoundPipeAndHostedOutboxSurviveColdRestartWithoutReordering()
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
                             PipeOptions.Asynchronous))
            {
                await partialFrame.ConnectAsync(5000);
                await partialFrame.WriteAsync(new byte[] { 1, 0 });
                await partialFrame.FlushAsync();
                await Task.Delay(400);
            }

            var response = await new StationMaterialArrivalLocalIpcClient(
                    CreateIpcOptions(pipeName))
                .ReportAsync(signal, TimeSpan.FromSeconds(5));
            Assert.True(response.Accepted);
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
                    CreateIpcOptions(pipeName))
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
    public async Task CompleteFrameCanWaitBeyondRequestFrameDeadlineWithoutCancelingSubmission()
    {
        Directory.CreateDirectory(_root);
        var package = await BuildSignedPackageAsync();
        var databasePath = Path.Combine(_root, "delayed-deployment.sqlite");
        var pipeName = $"openlineops-material-delay-{Guid.NewGuid():N}";
        var requestFrameTimeout = TimeSpan.FromMilliseconds(100);
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            $"material-arrival/plc/{Guid.NewGuid():D}",
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.delayed-deployment",
            Now);
        var deploymentGate = new DeploymentResolutionGate();
        var publisher = new HostPublisher(fail: false);
        using var host = CreateHost(
            databasePath,
            pipeName,
            package,
            new MutableClock(Now.AddSeconds(1)),
            publisher,
            deploymentGate.Decorate,
            requestFrameTimeout);

        await host.StartAsync();
        try
        {
            var responseTask = new StationMaterialArrivalLocalIpcClient(
                    CreateIpcOptions(pipeName))
                .ReportAsync(signal, TimeSpan.FromSeconds(5))
                .AsTask();
            await deploymentGate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            using (var deadlineCrossed = new CancellationTokenSource(
                       TimeSpan.FromTicks(requestFrameTimeout.Ticks * 3)))
            {
                await WaitForCancellationAsync(deadlineCrossed.Token);
            }

            Assert.False(deploymentGate.CancellationObserved);
            deploymentGate.Release();

            var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(response.Accepted);
            Assert.False(response.Replayed);
            Assert.Equal(signal.MessageId, response.MessageId);
        }
        finally
        {
            deploymentGate.Release();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task CompleteRequestCanWaitBeyondClientConnectionDeadlineForAcknowledgement()
    {
        var pipeName = $"openlineops-material-client-deadline-{Guid.NewGuid():N}";
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            $"material-arrival/plc/{Guid.NewGuid():D}",
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.delayed-acknowledgement",
            Now);
        var requestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAcknowledgement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var serverPipe = CreateTestPipe(pipeName);
        var waitForConnection = serverPipe.WaitForConnectionAsync(safety.Token);
        var server = Task.Run(async () =>
        {
            await waitForConnection;
            var payload = await StationMaterialArrivalLocalIpcServer.ReadFrameAsync(
                serverPipe,
                64 * 1024,
                safety.Token);
            var submitted = JsonSerializer.Deserialize<StationMaterialArrivalSignal>(
                payload,
                JsonOptions);
            Assert.Equal(signal.MessageId, submitted?.MessageId);
            requestReceived.TrySetResult();
            await releaseAcknowledgement.Task.WaitAsync(safety.Token);
            await StationMaterialArrivalLocalIpcServer.WriteFrameAsync(
                serverPipe,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        messageId = signal.MessageId,
                        accepted = true,
                        replayed = false,
                        failureCode = (string?)null
                    }),
                safety.Token);
            await ReadMaterialArrivalResponseReceiptAsync(serverPipe, safety.Token);
        }, safety.Token);

        var connectTimeout = TimeSpan.FromMilliseconds(200);
        var responseTask = new StationMaterialArrivalLocalIpcClient(
                CreateIpcOptions(pipeName))
            .ReportAsync(signal, connectTimeout, safety.Token)
            .AsTask();
        try
        {
            await requestReceived.Task.WaitAsync(safety.Token);
            using (var deadlineCrossed = new CancellationTokenSource(
                       TimeSpan.FromTicks(connectTimeout.Ticks * 3)))
            {
                await WaitForCancellationAsync(deadlineCrossed.Token);
            }

            Assert.False(responseTask.IsCompleted);
            releaseAcknowledgement.TrySetResult();

            var response = await responseTask.WaitAsync(safety.Token);
            Assert.True(response.Accepted);
            Assert.False(response.Replayed);
            Assert.Equal(signal.MessageId, response.MessageId);
        }
        finally
        {
            releaseAcknowledgement.TrySetResult();
            await server;
        }
    }

    [Fact]
    public async Task ClientFailsWhenNoServerConnectsBeforeDeadline()
    {
        var pipeName = $"openlineops-material-missing-{Guid.NewGuid():N}";
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            $"material-arrival/plc/{Guid.NewGuid():D}",
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.missing-server",
            Now);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            new StationMaterialArrivalLocalIpcClient(
                    CreateIpcOptions(pipeName))
                .ReportAsync(signal, TimeSpan.FromMilliseconds(200), safety.Token)
                .AsTask());
    }

    [Fact]
    public async Task CallerCancellationRemainsAuthoritativeWhileWaitingForAcknowledgement()
    {
        var pipeName = $"openlineops-material-caller-cancel-{Guid.NewGuid():N}";
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            $"material-arrival/plc/{Guid.NewGuid():D}",
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.caller-cancel",
            Now);
        var requestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseServer = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        await using var serverPipe = CreateTestPipe(pipeName);
        var waitForConnection = serverPipe.WaitForConnectionAsync(safety.Token);
        var server = Task.Run(async () =>
        {
            await waitForConnection;
            await StationMaterialArrivalLocalIpcServer.ReadFrameAsync(
                serverPipe,
                64 * 1024,
                safety.Token);
            requestReceived.TrySetResult();
            await releaseServer.Task.WaitAsync(safety.Token);
        }, safety.Token);

        var responseTask = new StationMaterialArrivalLocalIpcClient(
                CreateIpcOptions(pipeName))
            .ReportAsync(signal, TimeSpan.FromSeconds(5), caller.Token)
            .AsTask();
        try
        {
            await requestReceived.Task.WaitAsync(safety.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
        }
        finally
        {
            releaseServer.TrySetResult();
            await server;
        }
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
            await ReadMaterialArrivalResponseReceiptAsync(second, timeout.Token);
        }, timeout.Token);

        var response = await new StationMaterialArrivalLocalIpcClient(
                CreateIpcOptions(pipeName))
            .ReportAsync(signal, TimeSpan.FromSeconds(5), timeout.Token);
        await server;

        Assert.True(response.Accepted);
        Assert.True(response.Replayed);
        Assert.Equal(signal.MessageId, response.MessageId);
    }

    [Fact]
    public void MaterialArrivalPipeNameIsDeterministicallyBoundToTheStationServiceSid()
    {
        const string firstSid = "S-1-5-80-123-456-789-1011-1213";
        const string secondSid = "S-1-5-80-123-456-789-1011-1214";

        var first = StationMaterialArrivalLocalIpcOptions.DerivePipeName(firstSid);

        Assert.Equal(first, StationMaterialArrivalLocalIpcOptions.DerivePipeName(firstSid));
        Assert.NotEqual(
            first,
            StationMaterialArrivalLocalIpcOptions.DerivePipeName(secondSid));
        Assert.StartsWith("openlineops-material-", first, StringComparison.Ordinal);
        Assert.Equal("openlineops-material-".Length + 64, first.Length);
        Assert.Throws<InvalidDataException>(() =>
            StationMaterialArrivalLocalIpcOptions.DerivePipeName(
                new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null).Value));
    }

    [Theory]
    [InlineData("wrong-message")]
    [InlineData("accepted-with-failure")]
    [InlineData("rejected-without-failure")]
    [InlineData("rejected-replay")]
    [InlineData("missing-replayed")]
    public async Task ClientRejectsUnboundOrInconsistentAcknowledgement(string responseCase)
    {
        var pipeName = $"openlineops-material-invalid-ack-{Guid.NewGuid():N}";
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            $"material-arrival/plc/{Guid.NewGuid():D}",
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.invalid-acknowledgement",
            Now);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var serverPipe = CreateTestPipe(pipeName);
        var server = Task.Run(async () =>
        {
            await serverPipe.WaitForConnectionAsync(timeout.Token);
            await StationMaterialArrivalLocalIpcServer.ReadFrameAsync(
                serverPipe,
                64 * 1024,
                timeout.Token);
            if (string.Equals(responseCase, "missing-replayed", StringComparison.Ordinal))
            {
                await StationMaterialArrivalLocalIpcServer.WriteFrameAsync(
                    serverPipe,
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            messageId = signal.MessageId,
                            accepted = true,
                            failureCode = (string?)null
                        },
                        JsonOptions),
                    timeout.Token);
                return;
            }

            var response = responseCase switch
            {
                "wrong-message" => new StationMaterialArrivalLocalIpcResponse(
                    Guid.NewGuid(),
                    Accepted: true,
                    Replayed: false,
                    FailureCode: null),
                "accepted-with-failure" => new StationMaterialArrivalLocalIpcResponse(
                    signal.MessageId,
                    Accepted: true,
                    Replayed: false,
                    FailureCode: "Agent.MaterialArrivalSignalRejected"),
                "rejected-without-failure" => new StationMaterialArrivalLocalIpcResponse(
                    signal.MessageId,
                    Accepted: false,
                    Replayed: false,
                    FailureCode: null),
                "rejected-replay" => new StationMaterialArrivalLocalIpcResponse(
                    signal.MessageId,
                    Accepted: false,
                    Replayed: true,
                    FailureCode: "Agent.MaterialArrivalSignalRejected"),
                _ => throw new InvalidOperationException(
                    $"Unknown response test case '{responseCase}'.")
            };
            await StationMaterialArrivalLocalIpcServer.WriteFrameAsync(
                serverPipe,
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                timeout.Token);
        }, timeout.Token);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new StationMaterialArrivalLocalIpcClient(CreateIpcOptions(pipeName))
                .ReportAsync(signal, TimeSpan.FromSeconds(5), timeout.Token)
                .AsTask());
        await server;
    }

    [Fact]
    public void IdentityBoundPipeHasOneProtectedOwnerAndAccessRule()
    {
        var principal = new SecurityIdentifier(CurrentUserSid());
        using var pipe = CreateTestPipe($"openlineops-material-acl-{Guid.NewGuid():N}");

        var security = pipe.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            principal,
            Assert.IsType<SecurityIdentifier>(
                security.GetOwner(typeof(SecurityIdentifier))));
        var rule = Assert.Single(
            security
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>());
        Assert.False(rule.IsInherited);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(principal, rule.IdentityReference);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
    }

    [Fact]
    public async Task ClientRejectsAConnectedPipeOwnedByAnotherConfiguredIdentity()
    {
        var pipeName = $"openlineops-material-wrong-owner-{Guid.NewGuid():N}";
        await using var server = CreateTestPipe(pipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = server.WaitForConnectionAsync(timeout.Token);
        var signal = new StationMaterialArrivalSignal(
            Guid.NewGuid(),
            $"material-arrival/plc/{Guid.NewGuid():D}",
            StationMaterialKinds.ProductionUnit,
            Guid.NewGuid().ToString("D"),
            StationMaterialArrivalSources.Plc,
            "plc.reader.wrong-owner",
            Now);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new StationMaterialArrivalLocalIpcClient(
                    new StationMaterialArrivalLocalIpcOptions(
                        pipeName,
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value))
                .ReportAsync(signal, TimeSpan.FromSeconds(2), timeout.Token)
                .AsTask());
        await connected;
    }

    [Fact]
    public void FirstPipeInstancePreventsNamePreemptionWhileBoundaryIsAlive()
    {
        var pipeName = $"openlineops-material-first-instance-{Guid.NewGuid():N}";
        using var first = CreateTestPipe(pipeName);

        var exception = Record.Exception(() => CreateTestPipe(pipeName));

        Assert.NotNull(exception);
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Unexpected duplicate pipe exception: {exception}");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static NamedPipeServerStream CreateTestPipe(string pipeName) =>
        WindowsIdentityBoundNamedPipe.CreateServer(
            pipeName,
            CurrentUserSid(),
            maximumServerInstances: 1,
            inputBufferSize: 64 * 1024 + sizeof(int),
            outputBufferSize: 4096);

    private static async ValueTask ReadMaterialArrivalResponseReceiptAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var receipt = new byte[1];
        await stream.ReadExactlyAsync(receipt, cancellationToken);
        Assert.Equal(0xA5, receipt[0]);
    }

    private static StationMaterialArrivalLocalIpcOptions CreateIpcOptions(
        string pipeName,
        TimeSpan requestFrameTimeout = default) =>
        new(
            pipeName,
            CurrentUserSid(),
            RequestFrameTimeout: requestFrameTimeout);

    private static string CurrentUserSid() =>
        WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User?.Value
        ?? throw new InvalidOperationException("Current test token has no user SID.");

    private IHost CreateHost(
        string databasePath,
        string pipeName,
        PackageFixture package,
        MutableClock clock,
        HostPublisher publisher,
        Func<IStationMaterialArrivalDeploymentProvider,
            IStationMaterialArrivalDeploymentProvider>? decorateDeployment = null,
        TimeSpan? requestFrameTimeout = null)
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
                },
                ImmutableStationServiceSid:
                    AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()),
            new InventoryOnlyTestContentProtector()));
        builder.Services.AddSingleton<IStationMaterialArrivalDeploymentProvider>(serviceProvider =>
        {
            var provider = new SignedStationMaterialArrivalDeploymentProvider(
                new SignedStationMaterialArrivalDeploymentOptions(
                    "agent.main",
                    "station.main",
                    package.PackagePath,
                    package.ContentSha256),
                serviceProvider.GetRequiredService<SignedStationPackageInstaller>());
            return decorateDeployment?.Invoke(provider) ?? provider;
        });
        builder.Services.AddSingleton<StationMaterialArrivalReporter>();
        builder.Services.AddSingleton<IStationAgentMessagePublisher>(publisher);
        builder.Services.AddSingleton<StationMaterialArrivalOutboxDispatcher>();
        builder.Services.AddSingleton(new StationMaterialArrivalLocalIpcOptions(
            pipeName,
            CurrentUserSid(),
            RequestFrameTimeout: requestFrameTimeout ?? TimeSpan.FromMilliseconds(200)));
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

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            canceled);
        await canceled.Task;
    }

    private sealed record PackageFixture(
        string PackagePath,
        string ContentSha256,
        string PublicKeyPem);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class DeploymentResolutionGate
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationObserved;

        public Task Entered => _entered.Task;

        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) == 1;

        public IStationMaterialArrivalDeploymentProvider Decorate(
            IStationMaterialArrivalDeploymentProvider inner) =>
            new GatedDeploymentProvider(this, inner);

        public void Release() => _released.TrySetResult();

        private sealed class GatedDeploymentProvider(
            DeploymentResolutionGate gate,
            IStationMaterialArrivalDeploymentProvider inner) :
            IStationMaterialArrivalDeploymentProvider
        {
            public async ValueTask<VerifiedStationMaterialArrivalDeployment> GetCurrentAsync(
                CancellationToken cancellationToken = default)
            {
                gate._entered.TrySetResult();
                using var registration = cancellationToken.Register(
                    static state => Interlocked.Exchange(
                        ref ((DeploymentResolutionGate)state!)._cancellationObserved,
                        1),
                    gate);
                await gate._released.Task.WaitAsync(cancellationToken);
                return await inner.GetCurrentAsync(cancellationToken);
            }
        }
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
