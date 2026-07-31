using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationController;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent.Tests;

public sealed class StationControllerCommandWorkerTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BootIdentityUsesFreshUuidV4AndRedactsLeaseProof()
    {
        var first = StationAgentProcessIdentity.Create("agent-a", "station-a");
        var second = StationAgentProcessIdentity.Create("agent-a", "station-a");

        Assert.NotEqual(first.OwnerInstanceId, second.OwnerInstanceId);
        Assert.NotEqual(first.LeaseHandle, second.LeaseHandle);
        Assert.Equal('4', first.OwnerInstanceId[14]);
        Assert.Equal(43, first.LeaseHandle.Length);
        Assert.DoesNotContain(first.LeaseHandle, first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            first.LeaseHandle,
            JsonSerializer.Serialize(first),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LostAcquireResponseRetryReusesBootProofWithoutServerEcho()
    {
        var identity = StationAgentProcessIdentity.Create("agent-a", "station-a");
        var handler = new LostFirstAcquireResponseHandler(identity);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://coordinator.invalid/")
        };
        var client = new HttpStationControllerCoordinatorClient(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.AcquireLeaseAsync(identity));
        var grant = await client.AcquireLeaseAsync(identity);

        Assert.Equal(2, handler.Handles.Count);
        Assert.All(handler.Handles, handle =>
            Assert.Equal(identity.LeaseHandle, handle));
        Assert.Equal(identity.LeaseHandle, grant.LeaseHandle);
        Assert.DoesNotContain(grant.LeaseHandle, grant.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            grant.LeaseHandle,
            JsonSerializer.Serialize(grant),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollBindsCommandToExactOwnerTupleAndOperationalEpoch()
    {
        var identity = Identity("11111111-1111-4111-8111-111111111111");
        var lease = Lease(identity, fencingToken: 23);
        var handler = new ControllerCommandHandler(identity, lease, ownerMatches: true);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://coordinator.invalid/")
        };
        var client = new HttpStationControllerCoordinatorClient(httpClient);

        var command = await client.PollCommandAsync(identity, lease);

        Assert.NotNull(command);
        Assert.Equal(identity.AgentId, command.OwnerAgentId);
        Assert.Equal(identity.OwnerInstanceId, command.OwnerInstanceId);
        Assert.Equal(17, command.IssuedOperationalEpoch);
        Assert.Equal(identity.LeaseHandle, handler.LeaseHandle);
        Assert.Contains(
            $"ownerInstanceId={identity.OwnerInstanceId}",
            handler.RequestUri!.Query,
            StringComparison.Ordinal);
        Assert.Contains(
            $"fencingToken={lease.FencingToken}",
            handler.RequestUri.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollRejectsCommandOwnedByAnotherAgentBoot()
    {
        var identity = Identity("11111111-1111-4111-8111-111111111111");
        var lease = Lease(identity, fencingToken: 23);
        var handler = new ControllerCommandHandler(identity, lease, ownerMatches: false);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://coordinator.invalid/")
        };
        var client = new HttpStationControllerCoordinatorClient(httpClient);

        await Assert.ThrowsAsync<StationAgentControlLeaseRejectedException>(async () =>
            await client.PollCommandAsync(identity, lease));
    }

    [Fact]
    public async Task DispatchVerifierRenewsExactProofBearingBootLease()
    {
        var identity = Identity("11111111-1111-4111-8111-111111111111");
        var lease = Lease(identity, fencingToken: 29);
        var state = new StationAgentControlLeaseState(identity);
        state.ActivateOrRenew(lease);
        var coordinator = new RecordingCoordinator();
        var verifier = new StationDispatchControlLeaseVerifier(
            "station-system-a",
            identity,
            state,
            coordinator,
            new FixedClock(BaseTimeUtc),
            TimeSpan.FromSeconds(1));

        var result = await verifier.ValidateCurrentAsync(
            new StationDispatchControlLeaseExpectation(
                identity.AgentId,
                identity.StationId,
                "station-system-a",
                new StationAgentControlLeaseDispatchAuthority(
                    identity.AgentId,
                    identity.OwnerInstanceId,
                    lease.FencingToken,
                    lease.ExpiresAtUtc)));

        Assert.True(result.Accepted);
        Assert.False(result.Retryable);
        Assert.Equal(1, coordinator.RenewalCount);
        Assert.True(state.TryGetActive(out var renewed));
        Assert.Equal(lease.ExpiresAtUtc.AddMinutes(1), renewed!.ExpiresAtUtc);
        Assert.DoesNotContain(identity.LeaseHandle, state.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchVerifierRejectsStaleGenerationWithoutRemoteCall()
    {
        var identity = Identity("11111111-1111-4111-8111-111111111111");
        var lease = Lease(identity, fencingToken: 31);
        var state = new StationAgentControlLeaseState(identity);
        state.ActivateOrRenew(lease);
        var coordinator = new RecordingCoordinator();
        var verifier = new StationDispatchControlLeaseVerifier(
            "station-system-a",
            identity,
            state,
            coordinator,
            new FixedClock(BaseTimeUtc),
            TimeSpan.FromSeconds(1));

        var result = await verifier.ValidateCurrentAsync(
            new StationDispatchControlLeaseExpectation(
                identity.AgentId,
                identity.StationId,
                "station-system-a",
                new StationAgentControlLeaseDispatchAuthority(
                    identity.AgentId,
                    identity.OwnerInstanceId,
                    FencingToken: 30,
                    lease.ExpiresAtUtc)));

        Assert.False(result.Accepted);
        Assert.False(result.Retryable);
        Assert.Equal(0, coordinator.RenewalCount);
    }

    [Fact]
    public async Task DispatchVerifierReportsUnavailableWithoutActiveBootProof()
    {
        var identity = Identity("11111111-1111-4111-8111-111111111111");
        var lease = Lease(identity, fencingToken: 33);
        var coordinator = new RecordingCoordinator();
        var verifier = new StationDispatchControlLeaseVerifier(
            "station-system-a",
            identity,
            new StationAgentControlLeaseState(identity),
            coordinator,
            new FixedClock(BaseTimeUtc),
            TimeSpan.FromSeconds(1));

        var result = await verifier.ValidateCurrentAsync(
            new StationDispatchControlLeaseExpectation(
                identity.AgentId,
                identity.StationId,
                "station-system-a",
                new StationAgentControlLeaseDispatchAuthority(
                    identity.AgentId,
                    identity.OwnerInstanceId,
                    lease.FencingToken,
                    lease.ExpiresAtUtc)));

        Assert.False(result.Accepted);
        Assert.True(result.Retryable);
        Assert.Equal(0, coordinator.RenewalCount);
    }

    [Fact]
    public async Task DurableHighWaterRejectsStaleAgentAfterRestart()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "agent.sqlite");
            var identity = Identity("11111111-1111-4111-8111-111111111111");
            var newerLease = Lease(identity, fencingToken: 2);
            var newerCommand = Command(
                identity,
                newerLease,
                "command-new",
                StationControllerCommandIdempotency.NonIdempotent);
            using (var journal = Journal(databasePath))
            {
                var accepted = await journal.TryAcceptAsync(
                    newerCommand,
                    Fingerprint(newerCommand),
                    BaseTimeUtc);
                Assert.Equal(
                    StationControllerCommandAcceptanceStatus.Accepted,
                    accepted.Status);
            }

            var staleLease = Lease(identity, fencingToken: 1);
            var staleCommand = Command(
                identity,
                staleLease,
                "command-old",
                StationControllerCommandIdempotency.NonIdempotent);
            var executor = new RecordingExecutor(Completed(staleCommand));
            using var restarted = Journal(databasePath);
            var worker = Worker(
                identity,
                restarted,
                executor,
                new RecordingCoordinator(),
                BaseTimeUtc);

            await Assert.ThrowsAsync<StationAgentControlLeaseRejectedException>(async () =>
                await worker.ProcessCommandAsync(staleLease, staleCommand));
            Assert.Equal(0, executor.ExecutionCount);
            Assert.Equal(2, await restarted.GetFencingTokenHighWaterAsync("station-a"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalNonIdempotentCommandReplaysEvidenceWithoutPhysicalExecution()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "agent.sqlite");
            var identity = Identity("11111111-1111-4111-8111-111111111111");
            var lease = Lease(identity, fencingToken: 7);
            var command = Command(
                identity,
                lease,
                "command-terminal",
                StationControllerCommandIdempotency.NonIdempotent);
            var firstCoordinator = new RecordingCoordinator();
            var firstExecutor = new RecordingExecutor(Completed(command));
            using (var firstJournal = Journal(databasePath))
            {
                await Worker(
                        identity,
                        firstJournal,
                        firstExecutor,
                        firstCoordinator,
                        BaseTimeUtc)
                    .ProcessCommandAsync(lease, command);
            }

            Assert.Equal(1, firstExecutor.ExecutionCount);
            Assert.Equal(2, firstCoordinator.RenewalCount);
            Assert.Single(firstCoordinator.Reports);
            Assert.Single(firstCoordinator.Acknowledgements);

            var replayCoordinator = new RecordingCoordinator();
            var replayExecutor = new RecordingExecutor(Completed(command));
            using (var restartedJournal = Journal(databasePath))
            {
                await Worker(
                        identity,
                        restartedJournal,
                        replayExecutor,
                        replayCoordinator,
                        BaseTimeUtc.AddSeconds(1))
                    .ProcessCommandAsync(lease, command);
                var persisted = await restartedJournal.GetAsync(
                    command.StationId,
                    command.CommandId);
                Assert.Equal(
                    StationControllerCommandJournalStatus.Completed,
                    persisted!.Status);
                Assert.Equal(1, persisted.InvocationAttemptCount);
            }

            Assert.Equal(0, replayExecutor.ExecutionCount);
            Assert.Equal(0, replayCoordinator.RenewalCount);
            Assert.Single(replayCoordinator.Reports);
            Assert.Single(replayCoordinator.Acknowledgements);
            Assert.Equal(
                firstCoordinator.Reports[0],
                replayCoordinator.Reports[0]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestartFromInvokingNonIdempotentCommandBecomesUnknownWithoutReplay()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "agent.sqlite");
            var identity = Identity("11111111-1111-4111-8111-111111111111");
            var lease = Lease(identity, fencingToken: 9);
            var command = Command(
                identity,
                lease,
                "command-interrupted",
                StationControllerCommandIdempotency.NonIdempotent);
            var sha256 = Fingerprint(command);
            using (var crashedJournal = Journal(databasePath))
            {
                _ = await crashedJournal.TryAcceptAsync(
                    command,
                    sha256,
                    BaseTimeUtc);
                var claim = await crashedJournal.MarkInvokingAsync(
                    command.StationId,
                    command.CommandId,
                    sha256,
                    StationControllerCommandJournalStatus.Accepted,
                    expectedInvocationAttemptCount: 0,
                    BaseTimeUtc);
                Assert.True(claim.Claimed);
            }

            var coordinator = new RecordingCoordinator();
            var executor = new RecordingExecutor(
                new StationPhysicalControllerExecutionResult(
                    StationPhysicalControllerExecutionOutcome.CompletionUnknown,
                    Observation(command) with
                    {
                        Busy = true,
                        Completed = false
                    },
                    "Agent.ControllerCommandRecoveryRequired",
                    "Synthetic interrupted invocation."));
            using (var restartedJournal = Journal(databasePath))
            {
                await Worker(
                        identity,
                        restartedJournal,
                        executor,
                        coordinator,
                        BaseTimeUtc.AddSeconds(1))
                    .ProcessCommandAsync(lease, command);
                var persisted = await restartedJournal.GetAsync(
                    command.StationId,
                    command.CommandId);
                Assert.Equal(
                    StationControllerCommandJournalStatus.CompletionUnknown,
                    persisted!.Status);
                Assert.Equal(1, persisted.InvocationAttemptCount);
                Assert.Equal(
                    "Agent.ControllerCommandRecoveryRequired",
                    persisted.Result!.ErrorCode);
            }

            Assert.Equal(0, executor.ExecutionCount);
            var report = Assert.Single(coordinator.Reports);
            Assert.False(report.Error);
            Assert.False(report.Completed);
            Assert.True(report.Busy);
            Assert.Empty(coordinator.Acknowledgements);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PhysicalTimeoutPersistsUnknownAndNeverAcknowledges()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "agent.sqlite");
            var identity = Identity("11111111-1111-4111-8111-111111111111");
            var lease = Lease(identity, fencingToken: 11);
            var command = Command(
                identity,
                lease,
                "command-timeout",
                StationControllerCommandIdempotency.NonIdempotent);
            var coordinator = new RecordingCoordinator();
            var executor = new TimeoutExecutor(Observation(command) with
            {
                Busy = true,
                Completed = false
            });
            using var journal = Journal(databasePath);
            var worker = Worker(
                identity,
                journal,
                executor,
                coordinator,
                BaseTimeUtc,
                physicalTimeout: TimeSpan.FromMilliseconds(100));

            await worker.ProcessCommandAsync(lease, command);
            await worker.ProcessCommandAsync(lease, command);

            var persisted = await journal.GetAsync(
                command.StationId,
                command.CommandId);
            Assert.Equal(
                StationControllerCommandJournalStatus.CompletionUnknown,
                persisted!.Status);
            Assert.Equal(1, persisted.InvocationAttemptCount);
            Assert.Equal(1, executor.ExecutionCount);
            Assert.Equal(2, coordinator.Reports.Count);
            Assert.All(coordinator.Reports, report => Assert.True(report.Busy));
            Assert.Empty(coordinator.Acknowledgements);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LeaseHandleIsNeverPersistedInCommandJournal()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "agent.sqlite");
            var identity = Identity("11111111-1111-4111-8111-111111111111");
            var lease = Lease(identity, fencingToken: 15);
            var command = Command(
                identity,
                lease,
                "command-secret-check",
                StationControllerCommandIdempotency.NonIdempotent);
            using (var journal = Journal(databasePath))
            {
                await Worker(
                        identity,
                        journal,
                        new RecordingExecutor(Completed(command)),
                        new RecordingCoordinator(),
                        BaseTimeUtc)
                    .ProcessCommandAsync(lease, command);
            }

            SqliteConnection.ClearAllPools();
            var bytes = await File.ReadAllBytesAsync(databasePath);
            Assert.Equal(
                -1,
                bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(identity.LeaseHandle)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static StationControllerCommandWorker Worker(
        StationAgentProcessIdentity identity,
        IStationControllerCommandJournal journal,
        IStationPhysicalControllerExecutor executor,
        IStationControllerCoordinatorClient coordinator,
        DateTimeOffset nowUtc,
        TimeSpan? physicalTimeout = null) => new(
            identity,
            coordinator,
            journal,
            executor,
            new FixedClock(nowUtc),
            new StationControllerCommandWorkerOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                RenewalLeadTime = TimeSpan.FromSeconds(1),
                CoordinatorRequestTimeout = TimeSpan.FromSeconds(1),
                PhysicalExecutionTimeout = physicalTimeout ?? TimeSpan.FromSeconds(1),
                PersistenceTimeout = TimeSpan.FromSeconds(1),
                ShutdownReleaseTimeout = TimeSpan.FromSeconds(1)
            });

    private static StationAgentProcessIdentity Identity(string ownerInstanceId) =>
        new(
            "agent-a",
            "station-a",
            Guid.ParseExact(ownerInstanceId, "D"));

    private static StationAgentControlLeaseGrant Lease(
        StationAgentProcessIdentity identity,
        long fencingToken) => new(
            identity.StationId,
            identity.OwnerInstanceId,
            fencingToken,
            BaseTimeUtc.AddMinutes(1),
            identity.LeaseHandle);

    private static StationControllerCommandEnvelope Command(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        string commandId,
        StationControllerCommandIdempotency idempotency) => new(
            identity.StationId,
            identity.AgentId,
            identity.OwnerInstanceId,
            commandId,
            "controller-session-a",
            CommandSequence: 4,
            lease.FencingToken,
            Trigger: "Start",
            ExpectedMode: "Automatic",
            ExpectedCompletionState: "Execute",
            idempotency,
            SafetyClass: "Operational",
            ConfirmedRecipeId: "recipe-a",
            ConfirmedRecipeVersion: "1",
            BaseTimeUtc,
            BaseTimeUtc.AddSeconds(30),
            IssuedOperationalEpoch: 1,
            RecipeAssignmentId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            RecipeDeploymentId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            RecipeConfigurationSha256: new string('c', 64));

    private static StationControllerObservation Observation(
        StationControllerCommandEnvelope command) => new(
            command.ControllerSessionId,
            HeartbeatSequence: 8,
            command.CommandSequence,
            AcknowledgedCommandSequence: command.CommandSequence,
            command.ExpectedMode,
            command.ExpectedCompletionState,
            StateSequence: 3,
            Busy: false,
            Completed: true,
            Error: false,
            ErrorCode: null,
            RecipeConfirmed: true,
            command.ConfirmedRecipeId,
            command.ConfirmedRecipeVersion,
            command.RecipeAssignmentId,
            command.RecipeDeploymentId,
            command.RecipeConfigurationSha256,
            SafetyPermitGranted: true,
            ObservedAtUtc: BaseTimeUtc);

    private static StationPhysicalControllerExecutionResult Completed(
        StationControllerCommandEnvelope command) => new(
            StationPhysicalControllerExecutionOutcome.Completed,
            Observation(command),
            ErrorCode: null,
            ErrorReason: null);

    private static string Fingerprint(StationControllerCommandEnvelope command)
    {
        return StationControllerCommandFingerprint.Compute(command);
    }

    private static SqliteStationControllerCommandJournal Journal(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "openlineops-controller-worker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingCoordinator : IStationControllerCoordinatorClient
    {
        public List<StationControllerHandshakeReport> Reports { get; } = [];

        public List<StationControllerCommandAcknowledgement> Acknowledgements { get; } = [];

        public int RenewalCount { get; private set; }

        public ValueTask<StationAgentControlLeaseGrant> AcquireLeaseAsync(
            StationAgentProcessIdentity identity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DateTimeOffset> RenewLeaseAsync(
            StationAgentProcessIdentity identity,
            StationAgentControlLeaseGrant lease,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenewalCount++;
            return ValueTask.FromResult(lease.ExpiresAtUtc.AddMinutes(1));
        }

        public ValueTask ReleaseLeaseAsync(
            StationAgentProcessIdentity identity,
            StationAgentControlLeaseGrant lease,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<StationControllerCommandEnvelope?> PollCommandAsync(
            StationAgentProcessIdentity identity,
            StationAgentControlLeaseGrant lease,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ReportHandshakeAsync(
            StationAgentProcessIdentity identity,
            StationAgentControlLeaseGrant lease,
            StationControllerHandshakeReport report,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reports.Add(report);
            return ValueTask.CompletedTask;
        }

        public ValueTask AcknowledgeCommandAsync(
            StationAgentProcessIdentity identity,
            StationAgentControlLeaseGrant lease,
            StationControllerCommandAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acknowledgements.Add(acknowledgement);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingExecutor(
        StationPhysicalControllerExecutionResult result) :
        IStationPhysicalControllerExecutor
    {
        public int ExecutionCount { get; private set; }

        public ValueTask<StationPhysicalControllerExecutionResult> ExecuteAsync(
            StationControllerCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return ValueTask.FromResult(result);
        }

        public ValueTask<StationControllerObservation> ObserveAsync(
            string stationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                result.Observation
                ?? throw new InvalidOperationException("Observation unavailable."));
        }
    }

    private sealed class TimeoutExecutor(StationControllerObservation observation) :
        IStationPhysicalControllerExecutor
    {
        public int ExecutionCount { get; private set; }

        public async ValueTask<StationPhysicalControllerExecutionResult> ExecuteAsync(
            StationControllerCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public ValueTask<StationControllerObservation> ObserveAsync(
            string stationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(observation);
        }
    }

    private sealed class LostFirstAcquireResponseHandler(
        StationAgentProcessIdentity identity) : HttpMessageHandler
    {
        public List<string?> Handles { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Handles.Add(request.Headers.TryGetValues(
                    HttpStationControllerCoordinatorClient.LeaseHandleHeaderName,
                    out var values)
                ? values.Single()
                : null);
            if (Handles.Count == 1)
            {
                throw new HttpRequestException("Synthetic lost response.");
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    stationId = identity.StationId,
                    ownerAgentId = identity.AgentId,
                    ownerInstanceId = identity.OwnerInstanceId,
                    fencingToken = 21,
                    active = true,
                    expiresAtUtc = BaseTimeUtc.AddMinutes(1)
                })
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ControllerCommandHandler(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        bool ownerMatches) : HttpMessageHandler
    {
        public string? LeaseHandle { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            LeaseHandle = request.Headers.TryGetValues(
                    HttpStationControllerCoordinatorClient.LeaseHandleHeaderName,
                    out var values)
                ? values.Single()
                : null;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    stationId = identity.StationId,
                    lifecycleRevision = 5,
                    command = new
                    {
                        commandId = "command-http",
                        controllerSessionId = "controller-session-a",
                        expectedCommandSequence = 4,
                        ownerAgentId = identity.AgentId,
                        ownerAgentInstanceId = ownerMatches
                            ? identity.OwnerInstanceId
                            : "22222222-2222-4222-8222-222222222222",
                        fencingToken = lease.FencingToken,
                        trigger = "Start",
                        expectedMode = "Automatic",
                        expectedCompletionState = "Execute",
                        idempotency = "NonIdempotent",
                        safetyClass = "Operational",
                        confirmedRecipeId = "recipe-a",
                        confirmedRecipeVersion = "1",
                        issuedAtUtc = BaseTimeUtc,
                        deadlineUtc = BaseTimeUtc.AddSeconds(30),
                        issuedOperationalEpoch = 17,
                        recipeAssignmentId =
                            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                        recipeDeploymentId =
                            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                        recipeConfigurationSha256 = new string('c', 64)
                    }
                })
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
