extern alias OpenLineOpsApi;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.ContentProtection;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Infrastructure.ExternalPrograms;
using OpenLineOps.Projects.Infrastructure.Releases;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Time;
using OpenLineOps.Runtime.Infrastructure.Transport;
using RabbitMQ.Client;
using CoordinatorApiProgram = OpenLineOpsApi::Program;

namespace OpenLineOps.Agent.Tests;

public sealed partial class StagedAgentRabbitMqProcessE2ETests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private const string AgentBundleRootVariable = "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT";
    private const string SamplePluginRootVariable = "OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT";
    private const string ApiBundleRootVariable = "OPENLINEOPS_STAGED_API_BUNDLE_ROOT";
    private const string BrokerUriVariable = "OPENLINEOPS_RABBITMQ_URI";
    private const string EvidencePathVariable =
        "OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH";
    private const string ApplicationId = "application.rabbitmq-e2e";
    private const string FlowDefinitionId = "process.rabbitmq-e2e";
    private const string FlowVersionId = "process.rabbitmq-e2e@1";
    private const string LineDefinitionId = "line.rabbitmq-e2e";
    private const string OperationId = "operation.rabbitmq-e2e";
    private const string StationSystemId = "station.rabbitmq-e2e";
    private const string TopologyId = "topology.rabbitmq-e2e";
    private const string SigningKeyId = "staged-rabbitmq-e2e-signing";
    private const string ExternalProgramResourceId = "program.vendor-helper";
    private const string ExternalProgramCapabilityId = "application.external-program";
    private const string ExternalProgramCommandName = "Run";
    private const long VendorDelayMilliseconds = 15_000;
    private const string BrokerOutageControlVariable =
        "OPENLINEOPS_RABBITMQ_OUTAGE_CONTROL";
    private const string AgentServiceScopeVariable =
        "OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE";
    private const string AgentServiceCleanupGateVariable =
        "OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE";
    private const string AgentServiceCleanupManifestPathVariable =
        "OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH";
    private const string AgentServiceExternalAbortGateVariable =
        "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_GATE";
    private const string AgentServiceExternalAbortReadyPathVariable =
        "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_READY_PATH";

    [Fact]
    public void NonAdministrativeTokenEvidenceRequiresIndependentAdministratorMembershipProof()
    {
        var evidence = new AgentHostTokenEvidence(
            "machine\\standard-user",
            "S-1-5-21-1-2-3-1001",
            IsPrimaryToken: true,
            IsElevated: false,
            HasRestrictions: false,
            AdministratorGroupPresent: false,
            AdministratorGroupEnabled: false,
            AdministratorGroupDenyOnly: false,
            PrincipalAdministratorMembership: false,
            IsAuthenticated: true,
            IsSystem: false);

        Assert.True(evidence.NonAdministrative);
        Assert.False((evidence with
        {
            PrincipalAdministratorMembership = true
        }).NonAdministrative);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProfileDeletionRetryPolicyIsExplicitAndFailClosed()
    {
        Assert.True(RestrictedAgentIdentity.IsTransientProfileDeletionError(5));
        Assert.True(RestrictedAgentIdentity.IsTransientProfileDeletionError(32));
        Assert.True(RestrictedAgentIdentity.IsTransientProfileDeletionError(33));
        Assert.True(RestrictedAgentIdentity.IsTransientProfileDeletionError(170));
        Assert.True(RestrictedAgentIdentity.IsTransientProfileDeletionError(1224));

        Assert.False(RestrictedAgentIdentity.IsTransientProfileDeletionError(0));
        Assert.False(RestrictedAgentIdentity.IsTransientProfileDeletionError(2));
        Assert.False(RestrictedAgentIdentity.IsTransientProfileDeletionError(3));
        Assert.False(RestrictedAgentIdentity.IsTransientProfileDeletionError(87));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StagedAgentBuffersSignedVendorResultDuringBrokerOutageAndDeduplicatesAcrossRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prerequisites = ResolvePrerequisites();
        if (prerequisites is null)
        {
            return;
        }

        var suffix = RequireLowerHexScope(
            Environment.GetEnvironmentVariable(AgentServiceScopeVariable),
            AgentServiceScopeVariable);
        var cleanupContract = ReadRequiredAgentServiceCleanupContract(
            expectedKind: "rabbitmq",
            expectedScope: suffix);
        var cleanupEntry = Assert.Single(cleanupContract.Entries);
        var root = cleanupEntry.OwnedRoot;
        var ownedAgentBundleRoot = Path.Combine(root, "agent-bundle");
        Directory.CreateDirectory(root);
        ProtectRunScopedRoot(root);
        CopyFrozenAgentBundle(prerequisites.AgentBundleRoot, ownedAgentBundleRoot);
        ProtectFrozenBundleRoot(ownedAgentBundleRoot);
        VerifyCleanupExecutable(cleanupEntry);
        var executionPrerequisites = prerequisites with
        {
            AgentBundleRoot = ownedAgentBundleRoot
        };
        var agentId = $"agent.rabbitmq-e2e.{suffix}";
        var stationId = $"station.rabbitmq-e2e.{suffix}";
        var coordinatorId = $"coordinator-rabbitmq-e2e-{suffix}";
        var agentIdentity = RestrictedAgentIdentity.CreateRequired(suffix);
        var dataRoot = Path.Combine(root, "agent-data");
        var distributionRoot = Path.Combine(root, "package-distribution");
        var runtimeWorkRoot = Path.Combine(root, "runtime-work");
        var packageCacheRoot = Path.Combine(root, "package-cache");
        StagedCoordinatorApiProcess? artifactApi = null;
        HttpClient? operatorTraceClient = null;
        WindowsAgentService? agentService = null;
        WindowsAgentProcess? agent = null;
        CancellationTokenSource? resultInboxStop = null;
        Task? resultInbox = null;
        RabbitMqStationCoordinatorTransport? coordinator = null;
        BrokerTopology? topology = null;
        SqliteStationJobStore? stationStore = null;
        RabbitMqWindowsServiceOutage? brokerOutage = null;
        AgentHostTokenEvidence? initialAgentTokenEvidence = null;
        var offlinePendingOutboxCount = 0;
        var coordinatorTransportResultInboxRestartedAfterBrokerRecovery = false;
        Exception? executionFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            agentIdentity.GrantDirectoryAccess(
                root,
                FileSystemRights.Modify | FileSystemRights.Synchronize);
            agentIdentity.GrantDirectoryAccess(
                ownedAgentBundleRoot,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
            var artifactApiCredentials = ArtifactApiCredentials.Create(suffix, agentId, stationId);
            artifactApi = await StagedCoordinatorApiProcess.StartAsync(
                prerequisites.ApiBundleRoot,
                root,
                artifactApiCredentials,
                TimeSpan.FromSeconds(30));
            operatorTraceClient = artifactApi.CreateAuthenticatedClient(
                artifactApiCredentials.OperatorToken);
            var package = await BuildSignedVendorPackageAsync(root, distributionRoot);
            var safetyExecutable = PrepareSafetyNoOp(root);
            var request = CreateRequest(
                agentId,
                stationId,
                package.PackageContentSha256,
                suffix);
            var leaseChange = StationDispatchMessageIdentity.CreateLeaseGranted(
                request,
                Assert.Single(request.ResourceFences));
            var coordinationStore = new InMemoryStationJobCoordinationStore();
            Assert.True(await coordinationStore.TryEnqueueAsync(request, [leaseChange]));
            Assert.True(await artifactApi.CoordinationStore.TryEnqueueAsync(
                request,
                [leaseChange]));

            var transportOptions = new StationCoordinatorTransportOptions
            {
                BrokerUri = prerequisites.BrokerUri.AbsoluteUri,
                RequireTls = IsTls(prerequisites.BrokerUri),
                CoordinatorId = coordinatorId,
                Deployments =
                [
                    new StationDeploymentOptions
                    {
                        ProjectId = "project-main",
                        ApplicationId = "application-main",
                        AgentId = agentId,
                        StationId = stationId,
                        StationSystemId = StationSystemId
                    }
                ]
            };
            topology = await BrokerTopology.CreateAsync(
                prerequisites.BrokerUri,
                transportOptions,
                agentId,
                stationId);
            var presenceRepository = new EvidenceAgentPresenceRepository();
            coordinator = new RabbitMqStationCoordinatorTransport(
                transportOptions,
                coordinationStore,
                presenceRepository,
                new RemoteTraceStationArtifactReceiptVerifier(operatorTraceClient),
                new SystemClock());
            resultInboxStop = new CancellationTokenSource();
            resultInbox = coordinator.RunResultInboxAsync(
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask;
                },
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask;
                },
                resultInboxStop.Token);

            var environment = CreateAgentEnvironment(
                executionPrerequisites,
                root,
                dataRoot,
                distributionRoot,
                packageCacheRoot,
                runtimeWorkRoot,
                package,
                safetyExecutable,
                agentId,
                stationId,
                suffix,
                agentIdentity.Sid,
                artifactApi.BaseUri,
                artifactApiCredentials.AgentToken);
            agentService = WindowsAgentService.Install(
                cleanupEntry.ExecutablePath,
                ownedAgentBundleRoot,
                environment,
                agentIdentity,
                suffix);
            agent = agentService.Start();
            initialAgentTokenEvidence = agent.TokenEvidence;
            await WaitForExternalAbortIfRequestedAsync(cleanupEntry, agent);
            await topology.WaitForAgentConsumerAsync(agent, TimeSpan.FromSeconds(30));

            var runtimeClock = new SystemClock();
            var runtimeMaterials = new InMemoryProductionMaterialRepository();
            Assert.True(await runtimeMaterials.TryAddAsync(SlotOccupancy.Register(
                new SlotAddress(LineDefinitionId, StationSystemId, "slot.presence"),
                DateTimeOffset.UtcNow)));
            var presenceReader = new ProductionLineRuntimeStateReader(
                new InMemoryProductionRunRepository(runtimeMaterials),
                runtimeMaterials,
                new InMemoryResourceLeaseRepository(runtimeClock),
                presenceRepository,
                new AgentPresenceMonitoringOptions
                {
                    PresenceRequired = true,
                    TimeToLive = TimeSpan.FromSeconds(3)
                },
                runtimeClock);
            ProductionLineStationState? onlineBeforeOutage = null;
            await WaitUntilAsync(
                async () =>
                {
                    onlineBeforeOutage = await ReadPresenceStationAsync(presenceReader);
                    var persisted = await presenceRepository.GetAsync(agentId, stationId);
                    return persisted is { State: AgentPresenceState.Heartbeat }
                           && presenceRepository.AcceptedStates.Contains(
                               AgentPresenceState.Started)
                           && presenceRepository.AcceptedStates.Contains(
                               AgentPresenceState.Heartbeat)
                           && onlineBeforeOutage.Status
                               == ProductionLineStationRuntimeStatus.Idle
                           && onlineBeforeOutage.AgentPresenceHealth
                               == ProductionLineAgentPresenceHealth.Online;
                },
                agent,
                TimeSpan.FromSeconds(30),
                "Coordinator did not persist both Started and Heartbeat presence before dispatch.");

            await coordinator.PublishAsync(leaseChange);
            await coordinator.PublishAsync(request);
            await WaitUntilAsync(
                async () => (await coordinationStore.ListEventsAsync(request.JobId))
                    .Any(item => item.Kind == nameof(StationJobAccepted)),
                agent,
                TimeSpan.FromSeconds(30),
                "Coordinator did not receive StationJobAccepted before the broker outage.");

            var sqlitePath = Path.Combine(dataRoot, "station-agent.sqlite");
            stationStore = new SqliteStationJobStore(
                $"Data Source={sqlitePath};Pooling=False");
            brokerOutage = RabbitMqWindowsServiceOutage.CreateRequired(
                prerequisites.BrokerUri);
            await brokerOutage.StopAsync(TimeSpan.FromSeconds(45));
            ProductionLineStationState? offlineDuringOutage = null;
            await WaitUntilAsync(
                async () =>
                {
                    offlineDuringOutage = await ReadPresenceStationAsync(presenceReader);
                    return offlineDuringOutage.Status
                               == ProductionLineStationRuntimeStatus.Offline
                           && offlineDuringOutage.AgentPresenceHealth
                               == ProductionLineAgentPresenceHealth.Expired
                           && offlineDuringOutage.AgentPresenceAge > TimeSpan.FromSeconds(3);
                },
                agent,
                TimeSpan.FromSeconds(30),
                "Agent presence did not expire and project the Station Offline while RabbitMQ was unavailable.");
            StationJobStatus? lastOfflineStatus = null;
            IReadOnlyCollection<string> lastOfflineOutboxKinds = [];
            try
            {
                await WaitUntilAsync(
                    async () =>
                    {
                        var persistedOffline = await stationStore
                            .GetByIdempotencyKeyAsync(request.IdempotencyKey);
                        lastOfflineStatus = persistedOffline?.Job.Status;
                        lastOfflineOutboxKinds = await ReadAllPendingOutboxKindsAsync(
                            sqlitePath);
                        offlinePendingOutboxCount = lastOfflineOutboxKinds.Count;
                        if (persistedOffline?.Job.Status is StationJobStatus.Failed
                                or StationJobStatus.TimedOut
                                or StationJobStatus.Canceled
                                or StationJobStatus.Rejected)
                        {
                            throw new InvalidDataException(
                                $"The staged Agent vendor job terminated as {persistedOffline.Job.Status} "
                                + $"({persistedOffline.Job.FailureCode}): "
                                + persistedOffline.Job.FailureReason);
                        }

                        return persistedOffline?.Job.Status == StationJobStatus.Completed
                               && lastOfflineOutboxKinds.Any(kind =>
                                   kind is StationAgentMessageKinds.JobCompleted
                                       or StationAgentMessageKinds
                                           .JobCompletionPendingArtifactTransfer);
                    },
                    agent,
                    TimeSpan.FromMinutes(2),
                    "Agent did not durably buffer the completed vendor result while RabbitMQ was offline.");
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    "Agent did not durably buffer the completed vendor result while RabbitMQ was offline. "
                    + $"Last status: {lastOfflineStatus?.ToString() ?? "missing"}; "
                    + "all unacknowledged Outbox kinds: "
                    + string.Join(", ", lastOfflineOutboxKinds),
                    exception);
            }
            Assert.Null(await coordinationStore.GetCompletionAsync(request.IdempotencyKey));
            Assert.True(offlinePendingOutboxCount > 0);

            await brokerOutage.StartAsync(TimeSpan.FromMinutes(2));
            resultInboxStop.Cancel();
            await ObserveCancellationAsync(resultInbox);
            resultInboxStop.Dispose();
            resultInboxStop = null;
            resultInbox = null;
            await coordinator.DisposeAsync();
            coordinator = null;
            await topology.DisposeAsync();
            topology = null;

            topology = await BrokerTopology.CreateAsync(
                prerequisites.BrokerUri,
                transportOptions,
                agentId,
                stationId);
            coordinator = new RabbitMqStationCoordinatorTransport(
                transportOptions,
                coordinationStore,
                presenceRepository,
                new RemoteTraceStationArtifactReceiptVerifier(operatorTraceClient),
                new SystemClock());
            resultInboxStop = new CancellationTokenSource();
            resultInbox = coordinator.RunResultInboxAsync(
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask;
                },
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask;
                },
                resultInboxStop.Token);
            coordinatorTransportResultInboxRestartedAfterBrokerRecovery = true;
            await topology.WaitForAgentConsumerAsync(agent, TimeSpan.FromSeconds(30));
            var completion = await WaitForCompletionAsync(
                coordinationStore,
                request,
                agent,
                TimeSpan.FromMinutes(2));
            AssertCompletion(request, completion);
            await VerifyCentralArtifactsAsync(operatorTraceClient, completion);
            ProductionLineStationState? onlineAfterReconnect = null;
            await WaitUntilAsync(
                async () =>
                {
                    onlineAfterReconnect = await ReadPresenceStationAsync(presenceReader);
                    var persisted = await presenceRepository.GetAsync(agentId, stationId);
                    return persisted is { State: AgentPresenceState.Heartbeat }
                           && persisted.ReceivedAtUtc
                               > offlineDuringOutage!.AgentPresenceLastSeenAtUtc
                           && onlineAfterReconnect.Status
                               == ProductionLineStationRuntimeStatus.Idle
                           && onlineAfterReconnect.AgentPresenceHealth
                               == ProductionLineAgentPresenceHealth.Online
                           && onlineAfterReconnect.AgentPresenceAge <= TimeSpan.FromSeconds(3);
                },
                agent,
                TimeSpan.FromSeconds(30),
                "A fresh persisted heartbeat did not restore the Station Online after RabbitMQ recovered.");

            await WaitUntilAsync(
                async () => (await ReadAllPendingOutboxKindsAsync(sqlitePath)).Count == 0,
                agent,
                TimeSpan.FromSeconds(30),
                "Agent result Outbox did not drain.");
            var persisted = await stationStore.GetByIdempotencyKeyAsync(request.IdempotencyKey)
                            ?? throw new InvalidDataException(
                                "The staged Agent did not persist its Station Job Inbox entry.");
            var initialRevision = persisted.Revision;
            IReadOnlyCollection<StationJobEventInboxItem> initialEvents = [];
            await WaitUntilAsync(
                async () =>
                {
                    initialEvents = await coordinationStore.ListEventsAsync(request.JobId);
                    return CountRuntimeFinishedExecutions(initialEvents) == 1
                           && initialEvents.Any(item =>
                               item.Kind == nameof(StationJobAccepted))
                           && initialEvents.Any(item =>
                               item.Kind == nameof(StationJobProgressed));
                },
                agent,
                TimeSpan.FromSeconds(30),
                "Coordinator did not persist the complete ordered Agent event timeline.");
            var initialExecutionCount = CountRuntimeFinishedExecutions(initialEvents);
            Assert.Equal(1, initialExecutionCount);
            Assert.Contains(initialEvents, item => item.Kind == nameof(StationJobAccepted));
            Assert.Contains(initialEvents, item => item.Kind == nameof(StationJobProgressed));

            await coordinator.PublishAsync(leaseChange);
            await coordinator.PublishAsync(request);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await AssertNoDuplicateAsync(
                stationStore,
                coordinationStore,
                request,
                initialRevision,
                initialEvents.Count,
                initialExecutionCount);

            var firstPresenceSessionId = Assert.Single(
                presenceRepository.AcceptedSnapshots
                    .Select(static presence => presence.SessionId)
                    .Distinct());
            var firstAgentPid = agent.Id;
            var firstExitCode = await agent.StopCleanlyAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, firstExitCode);
            await WaitForAcceptedStoppingAsync(
                presenceRepository,
                firstPresenceSessionId,
                TimeSpan.FromSeconds(5));
            AssertPresenceSession(presenceRepository, firstPresenceSessionId);
            agent.Dispose();
            agent = null;

            agent = agentService.Start();
            var restartedAgentTokenEvidence = agent.TokenEvidence;
            Assert.Equal(initialAgentTokenEvidence, restartedAgentTokenEvidence);
            var restartedAgentPid = agent.Id;
            Assert.NotEqual(firstAgentPid, restartedAgentPid);
            await topology.WaitForAgentConsumerAsync(agent, TimeSpan.FromSeconds(30));
            await coordinator.PublishAsync(leaseChange);
            await coordinator.PublishAsync(request);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await AssertNoDuplicateAsync(
                stationStore,
                coordinationStore,
                request,
                initialRevision,
                initialEvents.Count,
                initialExecutionCount);

            var restartedPresenceSessionId = Assert.Single(
                presenceRepository.AcceptedSnapshots
                    .Select(static presence => presence.SessionId)
                    .Distinct(),
                sessionId => sessionId != firstPresenceSessionId);
            Assert.NotEqual(firstPresenceSessionId, restartedPresenceSessionId);
            var restartExitCode = await agent.StopCleanlyAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, restartExitCode);
            await WaitForAcceptedStoppingAsync(
                presenceRepository,
                restartedPresenceSessionId,
                TimeSpan.FromSeconds(5));
            AssertPresenceSession(presenceRepository, restartedPresenceSessionId);
            agent.Dispose();
            agent = null;
            stationStore.Dispose();
            stationStore = null;
            var windowsServiceName = agentService.ServiceName;
            var windowsServiceLifecycleVerified = agentService.LifecycleVerified;
            Assert.True(windowsServiceLifecycleVerified);
            agentService.Dispose();
            Assert.True(agentService.DeletionProven);
            agentService = null;

            WriteEvidence(
                prerequisites,
                package,
                request,
                completion,
                sqlitePath,
                initialEvents,
                firstAgentPid,
                restartedAgentPid,
                firstExitCode,
                restartExitCode,
                initialRevision,
                initialExecutionCount,
                offlinePendingOutboxCount,
                presenceRepository.AcceptedSnapshots,
                onlineBeforeOutage!,
                offlineDuringOutage!,
                onlineAfterReconnect!,
                brokerOutage.ControlMode,
                coordinatorTransportResultInboxRestartedAfterBrokerRecovery,
                agentIdentity,
                windowsServiceName,
                windowsServiceLifecycleVerified,
                initialAgentTokenEvidence!,
                restartedAgentTokenEvidence);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
        }
        finally
        {
            await CaptureCleanupFailureAsync(
                cleanupFailures,
                async () =>
                {
                    if (brokerOutage is not null)
                    {
                        await brokerOutage.EnsureStartedAsync(TimeSpan.FromMinutes(2));
                    }
                });
            CaptureCleanupFailure(
                cleanupFailures,
                () => agent?.Kill());
            CaptureCleanupFailure(
                cleanupFailures,
                () => agent?.Dispose());
            agent = null;
            CaptureCleanupFailure(
                cleanupFailures,
                () => agentService?.Dispose());
            agentService = null;
            CaptureCleanupFailure(
                cleanupFailures,
                () => stationStore?.Dispose());
            stationStore = null;
            CaptureCleanupFailure(
                cleanupFailures,
                () => resultInboxStop?.Cancel());
            await CaptureCleanupFailureAsync(
                cleanupFailures,
                async () =>
                {
                    if (resultInbox is not null)
                    {
                        await ObserveCancellationAsync(resultInbox);
                    }
                });
            CaptureCleanupFailure(
                cleanupFailures,
                () => resultInboxStop?.Dispose());
            resultInboxStop = null;
            resultInbox = null;
            await CaptureCleanupFailureAsync(
                cleanupFailures,
                async () =>
                {
                    if (coordinator is not null)
                    {
                        await coordinator.DisposeAsync();
                    }
                });
            coordinator = null;
            await CaptureCleanupFailureAsync(
                cleanupFailures,
                async () =>
                {
                    if (topology is not null)
                    {
                        await topology.DeleteQueuesAsync();
                    }
                });
            await CaptureCleanupFailureAsync(
                cleanupFailures,
                async () =>
                {
                    if (topology is not null)
                    {
                        await topology.DisposeAsync();
                    }
                });
            topology = null;
            CaptureCleanupFailure(
                cleanupFailures,
                () => operatorTraceClient?.Dispose());
            operatorTraceClient = null;
            await CaptureCleanupFailureAsync(
                cleanupFailures,
                async () =>
                {
                    if (artifactApi is not null)
                    {
                        await artifactApi.DisposeAsync();
                    }
                });
            artifactApi = null;
            if (agentIdentity.HasUnprovenServiceInstallation)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "Staged Agent SCM deletion is unproven; the service account, "
                    + "SeServiceLogonRight, profile, ACLs, and working root were deliberately preserved for safe diagnosis."));
            }
            else
            {
                var identityCleanupFailureCount = cleanupFailures.Count;
                CaptureCleanupFailure(
                    cleanupFailures,
                    () => VerifyRunScopedOwnedRootForIdentityCleanup(
                        root,
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            agentIdentity.Sid
                        }));
                if (cleanupFailures.Count == identityCleanupFailureCount)
                {
                    CaptureCleanupFailure(
                        cleanupFailures,
                        agentIdentity.Dispose);
                }

                if (cleanupFailures.Count == identityCleanupFailureCount)
                {
                    CaptureCleanupFailure(
                        cleanupFailures,
                        () => DeleteRunScopedOwnedRoot("rabbitmq", root));
                }
            }
        }

        if (executionFailure is not null)
        {
            cleanupFailures.Insert(0, executionFailure);
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        if (cleanupFailures.Count > 1)
        {
            throw new AggregateException(
                "The staged Agent RabbitMQ E2E failed and one or more independent cleanup steps also failed.",
                cleanupFailures);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CleanupRunScopedWindowsAgentServicesAndIdentities()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var gate = Environment.GetEnvironmentVariable(AgentServiceCleanupGateVariable);
        if (gate is null)
        {
            return;
        }

        if (!string.Equals(gate, "true", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{AgentServiceCleanupGateVariable} must be exactly 'true'.");
        }

        CleanupRunScopedAgentServicesAndIdentities(
            ReadRequiredAgentServiceCleanupContract(
                expectedKind: null,
                expectedScope: null));
    }

    [SupportedOSPlatform("windows")]
    private static void CleanupRunScopedAgentServicesAndIdentities(
        AgentServiceCleanupContract contract)
    {
        var failures = new List<Exception>();
        var identities = new Dictionary<string, RestrictedAgentIdentity.RunScopedIdentity?>(
            StringComparer.Ordinal);
        var invalidIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in contract.Entries)
        {
            try
            {
                identities.Add(
                    entry.Role,
                    RestrictedAgentIdentity.ReadRunScopedIdentity(
                        entry.AccountName,
                        entry.AccountSid));
            }
            catch (Exception exception)
            {
                identities.Add(entry.Role, null);
                invalidIdentities.Add(entry.Role);
                failures.Add(new InvalidOperationException(
                    $"Run-scoped identity validation failed for role '{entry.Role}'; preserving that identity and owned root.",
                    exception));
            }
        }

        var deletedServices = new HashSet<string>(StringComparer.Ordinal);
        var deletedIdentities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in contract.Entries)
        {
            if (invalidIdentities.Contains(entry.Role))
            {
                continue;
            }

            try
            {
                var identity = identities[entry.Role];
                if (identity is null)
                {
                    WindowsAgentService.ProveRunScopedArtifactsAbsent(entry);
                }
                else
                {
                    WindowsAgentService.CleanupRunScoped(entry, identity.Sid);
                }

                deletedServices.Add(entry.Role);
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"Run-scoped service cleanup failed for role '{entry.Role}'; preserving its identity and owned root.",
                    exception));
            }
        }

        var authorizedRootSids = new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var rootGroup in contract.Entries.GroupBy(
                     static entry => entry.OwnedRoot,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (rootGroup.Any(entry => invalidIdentities.Contains(entry.Role)
                                       || !deletedServices.Contains(entry.Role)))
            {
                continue;
            }

            try
            {
                var allowedSids = rootGroup
                    .Select(entry => identities[entry.Role]?.Sid)
                    .Where(static sid => sid is not null)
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal);
                VerifyRunScopedOwnedRootForIdentityCleanup(
                    rootGroup.Key,
                    allowedSids);
                authorizedRootSids.Add(rootGroup.Key, allowedSids);
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"Run-scoped owned-root provenance validation failed for '{rootGroup.Key}'; preserving every identity in that root.",
                    exception));
            }
        }

        foreach (var rootGroup in contract.Entries.GroupBy(
                     static entry => entry.OwnedRoot,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!authorizedRootSids.TryGetValue(rootGroup.Key, out var allowedSids))
            {
                continue;
            }

            foreach (var entry in rootGroup)
            {
                try
                {
                    RestrictedAgentIdentity.CleanupRunScoped(
                        identities[entry.Role],
                        entry.AccountName,
                        entry.OwnedRoot,
                        allowedSids);
                    deletedIdentities.Add(entry.Role);
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidOperationException(
                        $"Run-scoped identity cleanup failed for role '{entry.Role}'; preserving its owned root.",
                        exception));
                }
            }
        }

        foreach (var rootGroup in contract.Entries.GroupBy(
                     static entry => entry.OwnedRoot,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (rootGroup.Any(entry => !deletedServices.Contains(entry.Role)
                                       || !deletedIdentities.Contains(entry.Role)))
            {
                continue;
            }

            try
            {
                DeleteRunScopedOwnedRoot(contract.Kind, rootGroup.Key);
                if (Directory.Exists(rootGroup.Key) || File.Exists(rootGroup.Key))
                {
                    throw new InvalidOperationException(
                        $"Run-scoped staged Agent root '{rootGroup.Key}' still exists after cleanup.");
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Run-scoped staged Agent cleanup was incomplete.",
                failures);
        }
    }

    [SupportedOSPlatform("windows")]
    private static AgentServiceCleanupContract ReadRequiredAgentServiceCleanupContract(
        string? expectedKind,
        string? expectedScope)
    {
        var manifestText = RequiredText(
            Environment.GetEnvironmentVariable(AgentServiceCleanupManifestPathVariable),
            AgentServiceCleanupManifestPathVariable);
        if (!Path.IsPathFullyQualified(manifestText))
        {
            throw new InvalidDataException(
                $"{AgentServiceCleanupManifestPathVariable} must be a canonical absolute path.");
        }

        var manifestPath = Path.GetFullPath(manifestText);
        if (!string.Equals(manifestPath, manifestText, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                $"{AgentServiceCleanupManifestPathVariable} must identify an existing canonical file.");
        }

        EnsureNoReparsePointInExistingPath(manifestPath, AgentServiceCleanupManifestPathVariable);
        VerifyCleanupManifestAcl(manifestPath);
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(manifestPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        var root = manifest.RootElement;
        RequireExactJsonProperties(
            root,
            "Agent service cleanup manifest",
            "schema",
            "schemaVersion",
            "kind",
            "scope",
            "entries");
        if (!string.Equals(
                RequiredJsonString(root, "schema"),
                "openlineops-agent-service-cleanup",
                StringComparison.Ordinal)
            || RequiredJsonInt32(root, "schemaVersion") != 1)
        {
            throw new InvalidDataException(
                "Agent service cleanup manifest schema is invalid.");
        }

        var kind = RequiredJsonString(root, "kind");
        if (kind is not ("rabbitmq" or "studio-two-agent")
            || expectedKind is not null
            && !string.Equals(kind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Agent service cleanup manifest kind is invalid.");
        }

        var scope = RequireLowerHexScope(RequiredJsonString(root, "scope"), "scope");
        if (expectedScope is not null
            && !string.Equals(scope, expectedScope, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Agent service cleanup manifest scope differs from the formal gate scope.");
        }

        var entriesElement = root.GetProperty("entries");
        if (entriesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Agent service cleanup manifest entries must be an array.");
        }

        var entries = entriesElement.EnumerateArray()
            .Select(element => ReadCleanupEntry(element, kind, scope))
            .ToArray();
        var expectedRoles = kind == "rabbitmq"
            ? new[] { "rabbitmq" }
            : new[] { "entry", "downstream" };
        if (!entries.Select(static entry => entry.Role)
                .SequenceEqual(expectedRoles, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Agent service cleanup manifest roles are missing, duplicated, or out of canonical order.");
        }

        return new AgentServiceCleanupContract(manifestPath, kind, scope, entries);
    }

    [SupportedOSPlatform("windows")]
    private static AgentServiceCleanupEntry ReadCleanupEntry(
        JsonElement element,
        string kind,
        string scope)
    {
        RequireExactJsonProperties(
            element,
            "Agent service cleanup entry",
            "role",
            "serviceSuffix",
            "accountSuffix",
            "serviceName",
            "accountName",
            "accountSid",
            "executablePath",
            "executableSha256",
            "ownedRoot");
        var role = RequiredJsonString(element, "role");
        var serviceSuffix = RequireLowerHexScope(
            RequiredJsonString(element, "serviceSuffix"),
            "serviceSuffix");
        var accountSuffix = RequireLowerHexScope(
            RequiredJsonString(element, "accountSuffix"),
            "accountSuffix");
        var expectedServiceSuffix = kind == "rabbitmq"
            ? scope
            : AgentServiceScopedSuffix($"{role}-service", scope);
        var expectedAccountSuffix = kind == "rabbitmq"
            ? scope
            : AgentServiceScopedSuffix($"{role}-account", scope);
        if (!string.Equals(serviceSuffix, expectedServiceSuffix, StringComparison.Ordinal)
            || !string.Equals(accountSuffix, expectedAccountSuffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Agent service cleanup entry '{role}' has a non-canonical derived suffix.");
        }

        var serviceName = RequiredJsonString(element, "serviceName");
        var accountName = RequiredJsonString(element, "accountName");
        if (!string.Equals(
                serviceName,
                $"OpenLineOpsAgentE2E-{serviceSuffix}",
                StringComparison.Ordinal)
            || !string.Equals(
                accountName,
                $"oloe2e{accountSuffix[..10]}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Agent service cleanup entry '{role}' has a non-canonical service or account name.");
        }

        var accountSidElement = element.GetProperty("accountSid");
        string? accountSid = null;
        if (accountSidElement.ValueKind == JsonValueKind.String)
        {
            accountSid = accountSidElement.GetString();
            if (string.IsNullOrWhiteSpace(accountSid)
                || !string.Equals(
                    new SecurityIdentifier(accountSid).Value,
                    accountSid,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Agent service cleanup entry '{role}' accountSid is not canonical.");
            }
        }
        else if (accountSidElement.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException(
                $"Agent service cleanup entry '{role}' accountSid must be null or a canonical SID string.");
        }

        var windowsTemp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Temp");
        var expectedOwnedRoot = Path.Combine(
            windowsTemp,
            kind == "rabbitmq"
                ? $"olo-staged-agent-rmq-{scope}"
                : $"olo-studio-two-agent-{scope}");
        var ownedRootText = RequiredJsonString(element, "ownedRoot");
        var ownedRoot = RequireCanonicalAbsolutePath(ownedRootText, "ownedRoot");
        if (!string.Equals(ownedRoot, expectedOwnedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Agent service cleanup entry '{role}' owned root is outside its exact run scope.");
        }

        var executablePath = RequireCanonicalAbsolutePath(
            RequiredJsonString(element, "executablePath"),
            "executablePath");
        var expectedExecutablePath = Path.Combine(
            ownedRoot,
            "agent-bundle",
            "OpenLineOps.Agent.exe");
        if (!string.Equals(
                executablePath,
                expectedExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Agent service cleanup entry '{role}' executable is outside its exact owned bundle.");
        }

        return new AgentServiceCleanupEntry(
            role,
            serviceSuffix,
            accountSuffix,
            serviceName,
            accountName,
            accountSid,
            executablePath,
            RequireLowerHex(
                RequiredJsonString(element, "executableSha256"),
                64,
                "executableSha256"),
            ownedRoot);
    }

    [SupportedOSPlatform("windows")]
    private static void UpdateCleanupManifestAccountSid(
        string accountSuffix,
        string accountName,
        string accountSid)
    {
        var contract = ReadRequiredAgentServiceCleanupContract(
            expectedKind: null,
            expectedScope: null);
        var matching = contract.Entries
            .Where(entry => string.Equals(
                entry.AccountSuffix,
                accountSuffix,
                StringComparison.Ordinal))
            .ToArray();
        var entry = matching.Length == 1
            ? matching[0]
            : throw new InvalidDataException(
                "Protected cleanup manifest does not contain exactly one entry for the new service account.");
        if (!string.Equals(entry.AccountName, accountName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Protected cleanup manifest account name differs from the newly created account.");
        }

        var canonicalSid = new SecurityIdentifier(accountSid).Value;
        if (entry.AccountSid is not null)
        {
            if (!string.Equals(entry.AccountSid, canonicalSid, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Protected cleanup manifest already records a different account SID.");
            }

            return;
        }

        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   bytes,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "openlineops-agent-service-cleanup");
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("kind", contract.Kind);
            writer.WriteString("scope", contract.Scope);
            writer.WriteStartArray("entries");
            foreach (var item in contract.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("role", item.Role);
                writer.WriteString("serviceSuffix", item.ServiceSuffix);
                writer.WriteString("accountSuffix", item.AccountSuffix);
                writer.WriteString("serviceName", item.ServiceName);
                writer.WriteString("accountName", item.AccountName);
                if (string.Equals(item.Role, entry.Role, StringComparison.Ordinal))
                {
                    writer.WriteString("accountSid", canonicalSid);
                }
                else if (item.AccountSid is null)
                {
                    writer.WriteNull("accountSid");
                }
                else
                {
                    writer.WriteString("accountSid", item.AccountSid);
                }

                writer.WriteString("executablePath", item.ExecutablePath);
                writer.WriteString("executableSha256", item.ExecutableSha256);
                writer.WriteString("ownedRoot", item.OwnedRoot);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        var temporaryPath = contract.ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (File.Create(temporaryPath))
            {
            }

            ProtectRunScopedFile(temporaryPath);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(bytes.GetBuffer(), 0, checked((int)bytes.Length));
                stream.SetLength(bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, contract.ManifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        var updated = ReadRequiredAgentServiceCleanupContract(
            expectedKind: contract.Kind,
            expectedScope: contract.Scope);
        var updatedEntry = Assert.Single(updated.Entries, item =>
            string.Equals(item.Role, entry.Role, StringComparison.Ordinal));
        if (!string.Equals(updatedEntry.AccountSid, canonicalSid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Protected cleanup manifest did not persist the exact new account SID.");
        }
    }

    private static void VerifyCleanupExecutable(AgentServiceCleanupEntry entry)
    {
        if (!File.Exists(entry.ExecutablePath))
        {
            throw new FileNotFoundException(
                "The copied run-scoped staged Agent executable is missing.",
                entry.ExecutablePath);
        }

        var actual = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(entry.ExecutablePath)));
        if (!string.Equals(actual, entry.ExecutableSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The copied run-scoped staged Agent executable differs from the cleanup manifest SHA-256.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForExternalAbortIfRequestedAsync(
        AgentServiceCleanupEntry entry,
        WindowsAgentProcess agent)
    {
        var gate = Environment.GetEnvironmentVariable(
            AgentServiceExternalAbortGateVariable);
        if (gate is null)
        {
            return;
        }

        if (!string.Equals(gate, "true", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{AgentServiceExternalAbortGateVariable} must be exactly 'true'.");
        }

        var readyText = RequiredText(
            Environment.GetEnvironmentVariable(
                AgentServiceExternalAbortReadyPathVariable),
            AgentServiceExternalAbortReadyPathVariable);
        var readyPath = RequireCanonicalAbsolutePath(
            readyText,
            AgentServiceExternalAbortReadyPathVariable);
        if (File.Exists(readyPath) || Directory.Exists(readyPath))
        {
            throw new InvalidOperationException(
                "External-abort ready marker path is not fresh.");
        }

        var parent = Path.GetDirectoryName(readyPath)
                     ?? throw new InvalidDataException(
                         "External-abort ready marker has no parent directory.");
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                $"External-abort ready marker parent '{parent}' is missing.");
        }

        EnsureNoReparsePointInExistingPath(parent, "external-abort ready marker parent");
        var temporaryPath = readyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (File.Create(temporaryPath))
            {
            }

            ProtectRunScopedFile(temporaryPath);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None))
            {
                using var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString(
                    "schema",
                    "openlineops-agent-service-external-abort-ready");
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("scope", entry.ServiceSuffix);
                writer.WriteString("serviceName", entry.ServiceName);
                writer.WriteString("accountName", entry.AccountName);
                writer.WriteNumber("testHostProcessId", Environment.ProcessId);
                writer.WriteNumber("agentProcessId", agent.Id);
                writer.WriteString("executablePath", agent.ExecutablePath);
                writer.WriteString("executableSha256", agent.ExecutableSha256);
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, readyPath, overwrite: false);
            VerifyCleanupManifestAcl(readyPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static void CopyFrozenAgentBundle(string sourceRoot, string destinationRoot)
    {
        var source = Path.GetFullPath(sourceRoot);
        var destination = Path.GetFullPath(destinationRoot);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"Staged Agent source bundle '{source}' is missing.");
        }

        EnsureNoReparsePointInExistingPath(source, "staged Agent source bundle");
        var sourcePaths = RejectTreeReparsePoints(
            source,
            "staged Agent source bundle");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new InvalidOperationException(
                $"Run-scoped staged Agent bundle destination '{destination}' is not fresh.");
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in sourcePaths
                     .Where(Directory.Exists)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (var file in sourcePaths
                     .Where(File.Exists)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var sourceHash = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(file)));
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target, overwrite: false);
            var targetHash = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(target)));
            if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Run-scoped staged Agent bundle copy changed '{Path.GetRelativePath(source, file)}'.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectRunScopedRoot(string root)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The staged Agent gate identity has no SID.");
        var security = new DirectorySecurity();
        security.SetOwner(currentSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(
                         WellKnownSidType.BuiltinAdministratorsSid,
                         null),
                     currentSid
                 }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), security);
        VerifyProtectedFileSystemAcl(
            FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(root),
                AccessControlSections.Owner | AccessControlSections.Access),
            currentSid,
            "run-scoped staged Agent root");
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFrozenBundleRoot(string bundleRoot)
    {
        ProtectRunScopedRoot(bundleRoot);
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     bundleRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectRunScopedFile(string path)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The staged Agent gate identity has no SID.");
        var security = new FileSecurity();
        security.SetOwner(currentSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(
                         WellKnownSidType.BuiltinAdministratorsSid,
                         null),
                     currentSid
                 }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
        VerifyProtectedFileSystemAcl(
            FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access),
            currentSid,
            "run-scoped staged Agent file");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyProtectedFileSystemAcl(
        FileSystemSecurity security,
        SecurityIdentifier expectedOwner,
        string name,
        IReadOnlySet<string>? allowedAdditionalSids = null)
    {
        if (!security.AreAccessRulesProtected
            || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !string.Equals(owner.Value, expectedOwner.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {name} ACL is inherited or has an unexpected owner.");
        }

        var requiredFullControl = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            expectedOwner.Value
        }.ToHashSet(StringComparer.Ordinal);
        var allowed = requiredFullControl.ToHashSet(StringComparer.Ordinal);
        if (allowedAdditionalSids is not null)
        {
            allowed.UnionWith(allowedAdditionalSids);
        }

        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Any(rule => rule.IsInherited
                              || rule.AccessControlType != AccessControlType.Allow
                              || rule.IdentityReference is not SecurityIdentifier sid
                              || !allowed.Contains(sid.Value)))
        {
            throw new InvalidOperationException(
                $"The {name} ACL contains an inherited, denied, or unexpected rule.");
        }

        foreach (var sid in requiredFullControl)
        {
            if (!rules.Any(rule => rule.IdentityReference is SecurityIdentifier identity
                                   && string.Equals(identity.Value, sid, StringComparison.Ordinal)
                                   && (rule.FileSystemRights & FileSystemRights.FullControl)
                                   == FileSystemRights.FullControl))
            {
                throw new InvalidOperationException(
                    $"The {name} ACL lacks FullControl for '{sid}'.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteRunScopedOwnedRoot(string kind, string root)
    {
        if (!Directory.Exists(root))
        {
            if (File.Exists(root))
            {
                throw new InvalidDataException(
                    $"Run-scoped staged Agent root '{root}' is a file; preserving it.");
            }

            return;
        }

        VerifyRunScopedOwnedRootForCleanup(root);
        if (kind == "studio-two-agent")
        {
            DeleteStudioAgentHarnessRoot(root);
            return;
        }

        DeleteWorkRoot(root, Path.Combine(root, "package-cache"));
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyRunScopedOwnedRootForCleanup(string root)
    {
        EnsureNoReparsePointInExistingPath(root, "run-scoped staged Agent root");
        RejectTreeReparsePoints(root, "run-scoped staged Agent root");
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The staged E2E cleanup identity has no SID for owned-root validation.");
        VerifyProtectedFileSystemAcl(
            FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(root),
                AccessControlSections.Owner | AccessControlSections.Access),
            currentSid,
            "run-scoped staged Agent root");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyRunScopedOwnedRootForIdentityCleanup(
        string root,
        IReadOnlySet<string> allowedAccountSids)
    {
        ArgumentNullException.ThrowIfNull(allowedAccountSids);
        foreach (var sid in allowedAccountSids)
        {
            _ = new SecurityIdentifier(sid);
        }

        if (!Directory.Exists(root))
        {
            if (File.Exists(root))
            {
                throw new InvalidDataException(
                    $"Run-scoped staged Agent root '{root}' is a file; preserving it.");
            }

            return;
        }

        EnsureNoReparsePointInExistingPath(root, "run-scoped staged Agent identity root");
        RejectTreeReparsePoints(root, "run-scoped staged Agent identity root");
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The staged E2E cleanup identity has no SID for identity-root validation.");
        VerifyProtectedFileSystemAcl(
            FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(root),
                AccessControlSections.Owner | AccessControlSections.Access),
            currentSid,
            "run-scoped staged Agent identity root",
            allowedAccountSids);
    }

    private static List<string> RejectTreeReparsePoints(
        string root,
        string name)
    {
        var proven = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        $"{name} contains a reparse point: {path}");
                }

                proven.Add(path);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(path);
                }
            }
        }

        return proven;
    }

    private static void EnsureNoReparsePointInExistingPath(string path, string name)
    {
        var fullPath = Path.GetFullPath(path);
        if ((File.Exists(fullPath) || Directory.Exists(fullPath))
            && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"{name} is a reparse point: {fullPath}");
        }

        var current = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath)
            : fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"{name} traverses a reparse point: {current}");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyCleanupManifestAcl(string manifestPath)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The cleanup process identity has no SID.");
        var security = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(manifestPath),
            AccessControlSections.Owner | AccessControlSections.Access);
        VerifyProtectedFileSystemAcl(
            security,
            currentSid,
            "Agent service cleanup manifest");
    }

    private static string RequireCanonicalAbsolutePath(string value, string name)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException($"{name} must be a canonical absolute path.");
        }

        var canonical = Path.GetFullPath(value);
        return string.Equals(canonical, value, StringComparison.OrdinalIgnoreCase)
            ? canonical
            : throw new InvalidDataException($"{name} must be canonical.");
    }

    private static string RequireLowerHexScope(string? value, string name) =>
        RequireLowerHex(RequiredText(value, name), 32, name);

    private static string RequireLowerHex(string value, int length, string name)
    {
        if (value.Length != length
            || value.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{name} must contain exactly {length} lowercase hexadecimal characters.");
        }

        return value;
    }

    private static string AgentServiceScopedSuffix(string role, string scope) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{role}:{scope}")))[..32];

    private static void RequireExactJsonProperties(
        JsonElement element,
        string name,
        params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{name} must be an object.");
        }

        var actual = element.EnumerateObject().Select(static property => property.Name).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{name} properties are missing, unknown, duplicated, or out of canonical order.");
        }
    }

    private static string RequiredJsonString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.String
               && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException(
                $"Agent service cleanup manifest '{propertyName}' must be a non-empty string.");
    }

    private static int RequiredJsonInt32(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException(
                $"Agent service cleanup manifest '{propertyName}' must be an Int32.");
    }

    private static StagedPrerequisites? ResolvePrerequisites()
    {
        var bundle = Environment.GetEnvironmentVariable(AgentBundleRootVariable);
        var plugin = Environment.GetEnvironmentVariable(SamplePluginRootVariable);
        var api = Environment.GetEnvironmentVariable(ApiBundleRootVariable);
        var broker = Environment.GetEnvironmentVariable(BrokerUriVariable);
        if (bundle is null && plugin is null && api is null && broker is null)
        {
            return null;
        }

        var bundleRoot = RequiredDirectory(bundle, AgentBundleRootVariable);
        var pluginRoot = RequiredDirectory(plugin, SamplePluginRootVariable);
        var apiRoot = RequiredDirectory(api, ApiBundleRootVariable);
        if (!Uri.TryCreate(RequiredText(broker, BrokerUriVariable), UriKind.Absolute, out var brokerUri)
            || brokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidDataException(
                $"{BrokerUriVariable} must be an absolute amqp or amqps URI.");
        }

        foreach (var fileName in new[]
                 {
                     "OpenLineOps.Agent.exe",
                     "OpenLineOps.StationRuntime.exe",
                     "OpenLineOps.PluginHost.exe",
                     "OpenLineOps.ScriptWorker.exe",
                     "OpenLineOps.LeastPrivilegeLauncher.exe"
                 })
        {
            _ = RequiredDirectFile(bundleRoot, fileName);
        }

        _ = RequiredDirectFile(pluginRoot, "manifest.json");
        _ = RequiredDirectFile(pluginRoot, "OpenLineOps.SamplePlugins.LoopbackDevice.dll");
        _ = RequiredDirectFile(apiRoot, "OpenLineOps.Api.exe");
        return new StagedPrerequisites(bundleRoot, pluginRoot, apiRoot, brokerUri);
    }

    private static async ValueTask<PublishedPackage> BuildSignedVendorPackageAsync(
        string root,
        string distributionRoot)
    {
        var scope = CreateApplication(root);
        var resource = await CreateExternalProgramResourceAsync(scope);
        var flow = CreateVendorFlow();
        var metadata = CreateVendorMetadata(flow, resource);
        var publishedAtUtc = DateTimeOffset.UtcNow;
        var release = await new FileSystemProjectReleaseArtifactStore().PublishAsync(
            scope,
            "snapshot.rabbitmq-e2e",
            publishedAtUtc,
            metadata);

        using var signingKey = RSA.Create(3072);
        var privateKeyPath = Path.Combine(root, "station-signing-private.pem");
        var publicKeyPath = Path.Combine(root, "station-signing-public.pem");
        await File.WriteAllTextAsync(privateKeyPath, signingKey.ExportRSAPrivateKeyPem());
        await File.WriteAllTextAsync(publicKeyPath, signingKey.ExportSubjectPublicKeyInfoPem());
        var publisher = new FileSystemProjectReleaseStationPackagePublisher(
            new StationPackagePublicationOptions(
                distributionRoot,
                Path.Combine(root, "deployment-catalog"),
                SigningKeyId,
                privateKeyPath));
        var stationPackage = Assert.Single((await publisher.PublishAsync(
            new ProjectReleaseStationPackageRequest(
                release,
                metadata,
                publishedAtUtc))).Packages);
        return new PublishedPackage(
            stationPackage.PackageContentSha256,
            stationPackage.PackagePath,
            publicKeyPath);
    }

    private static ProjectApplicationWorkspaceScope CreateApplication(string root)
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.rabbitmq-e2e",
            ApplicationId,
            Path.Combine(root, "project"),
            $"applications/{ApplicationId}/application.oloapp");
        Write(scope, "application.oloapp", "{}");
        Write(
            scope,
            "topology/topology.json",
            """
            {"schemaVersion":"openlineops.automation-topology","resourceKind":"OpenLineOps.AutomationTopology","applicationId":"application.rabbitmq-e2e","topologyId":"topology.rabbitmq-e2e"}
            """);
        Write(
            scope,
            "layouts/layout.main.json",
            """
            {"schemaVersion":"openlineops.site-layout","resourceKind":"OpenLineOps.SiteLayout","applicationId":"application.rabbitmq-e2e","layoutId":"layout.main"}
            """);
        Write(
            scope,
            $"production/lines/{LineDefinitionId}/line.json",
            """
            {"schemaVersion":"openlineops.production-line","resourceKind":"OpenLineOps.ProductionLine","applicationId":"application.rabbitmq-e2e","lineDefinitionId":"line.rabbitmq-e2e"}
            """);
        Write(
            scope,
            $"configuration/projects/{ResourceFileName("project", "engineering.rabbitmq-e2e")}",
            """
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.rabbitmq-e2e","resourceKind":"project","resourceId":"engineering.rabbitmq-e2e","snapshot":{"projectId":"engineering.rabbitmq-e2e","workspaceId":"workspace.rabbitmq-e2e","displayName":"RabbitMQ E2E","createdAtUtc":"2026-07-13T00:00:00+00:00","activeSnapshotId":"configuration.rabbitmq-e2e","snapshots":[{"snapshotId":"configuration.rabbitmq-e2e","projectId":"engineering.rabbitmq-e2e","processDefinitionId":"process.rabbitmq-e2e","processVersionId":"process.rabbitmq-e2e@1","recipeId":"recipe.rabbitmq-e2e","recipeVersionId":"recipe.rabbitmq-e2e@1","stationProfileId":"station.profile.rabbitmq-e2e","status":"Published","publishedAtUtc":"2026-07-13T00:00:00+00:00","deviceBindings":[{"deviceBindingId":"binding.loopback","ownerSystemId":"station.rabbitmq-e2e","capabilityId":"device.loopback","deviceKey":"loopback-device-01"}]}]}}
            """);
        Write(
            scope,
            $"configuration/station-profiles/{ResourceFileName("station-profile", "station.profile.rabbitmq-e2e")}",
            """
            {"schema":"openlineops.engineering-configuration-resource","schemaVersion":1,"applicationId":"application.rabbitmq-e2e","resourceKind":"station-profile","resourceId":"station.profile.rabbitmq-e2e","snapshot":{"stationProfileId":"station.profile.rabbitmq-e2e","stationSystemId":"station.rabbitmq-e2e","displayName":"RabbitMQ E2E Station","deviceBindings":[{"deviceBindingId":"binding.loopback","ownerSystemId":"station.rabbitmq-e2e","capabilityId":"device.loopback","deviceKey":"loopback-device-01"}]}}
            """);
        return scope;
    }

    private static FlowIrCanonicalArtifact CreateVendorFlow()
    {
        var source = new FlowIrSourceTrace(
            FlowDefinitionId,
            FlowVersionId,
            FlowIrSourceElementKind.ProcessNode,
            OperationId,
            null);
        var action = new FlowIrAction(
            $"{OperationId}:action:1",
            FlowIrActionKind.DeviceCommand,
            "Run frozen vendor helper",
            ExternalProgramCapabilityId,
            ExternalProgramCommandName,
            new FlowIrTargetReference(FlowIrTargetReferenceKind.System, StationSystemId),
            $"{{\"{ExternalProgramResourceContract.ResourceIdProperty}\":\"{ExternalProgramResourceId}\"}}",
            new FlowIrExecutionPolicy(
                VendorDelayMilliseconds + 30_000,
                0,
                FlowIrCancellationMode.Cooperative),
            null,
            source);
        var document = new FlowIrDocument(
            FlowIrSchema.Current,
            FlowDefinitionId,
            FlowVersionId,
            "Staged RabbitMQ vendor operation",
            "start",
            [
                new FlowIrNode(
                    "start",
                    FlowIrNodeKind.Start,
                    "Start",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "start",
                        null)),
                new FlowIrNode(
                    OperationId,
                    FlowIrNodeKind.Command,
                    "Run frozen vendor helper",
                    [action],
                    source),
                new FlowIrNode(
                    "end",
                    FlowIrNodeKind.End,
                    "End",
                    [],
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessNode,
                        "end",
                        null))
            ],
            [
                new FlowIrTransition(
                    "start-to-vendor",
                    "start",
                    OperationId,
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "start-to-vendor",
                        null)),
                new FlowIrTransition(
                    "vendor-to-end",
                    OperationId,
                    "end",
                    null,
                    FlowIrLoopPolicy.None,
                    null,
                    new FlowIrSourceTrace(
                        FlowDefinitionId,
                        FlowVersionId,
                        FlowIrSourceElementKind.ProcessTransition,
                        "vendor-to-end",
                        null))
            ],
            ImmutableArray<FlowIrBlockDependency>.Empty);
        var result = new FlowIrCanonicalSerializer().Serialize(document);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(result.Error.Message);
    }

    private static async ValueTask<ExternalProgramResource> CreateExternalProgramResourceAsync(
        ProjectApplicationWorkspaceScope scope)
    {
        var helperDirectory = VendorHelperOutputDirectory();
        var fileNames = new[]
        {
            "OpenLineOps.VendorTestHelper.exe",
            "OpenLineOps.VendorTestHelper.dll",
            "OpenLineOps.VendorTestHelper.deps.json",
            "OpenLineOps.VendorTestHelper.runtimeconfig.json"
        };
        var uploads = new List<ExternalProgramFileUpload>(fileNames.Length);
        foreach (var fileName in fileNames)
        {
            var bytes = await File.ReadAllBytesAsync(RequiredDirectFile(
                helperDirectory,
                fileName));
            uploads.Add(new ExternalProgramFileUpload(
                $"files/{fileName}",
                new MemoryStream(bytes, writable: false),
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        try
        {
            return await new FileSystemExternalProgramResourceRepository().SaveAsync(
                scope,
                new SaveExternalProgramResourceRequest(
                    ExternalProgramResourceId,
                    "Staged broker-outage vendor helper",
                    ExternalProgramCapabilityId,
                    ExternalProgramCommandName,
                    ExternalProgramLaunchKind.ApplicationExecutable,
                    "files/OpenLineOps.VendorTestHelper.exe",
                    ProviderKind: null,
                    ProviderKey: null,
                    [
                        "--mode",
                        "{{input.mode}}",
                        "--delay-milliseconds",
                        VendorDelayMilliseconds.ToString(CultureInfo.InvariantCulture)
                    ],
                    [
                        new ExternalProgramInputMapping("$product.identity", "mode"),
                        new ExternalProgramInputMapping("$product.model", "model")
                    ],
                    [
                        new ExternalProgramResultMapping(
                            "$.outcome",
                            "inspection.outcome",
                            ProductionContextValueKind.Text),
                        new ExternalProgramResultMapping(
                            "$.metrics.voltage",
                            "inspection.voltage",
                            ProductionContextValueKind.FixedPoint),
                        new ExternalProgramResultMapping(
                            "$.metrics.attempt",
                            "inspection.attempt",
                            ProductionContextValueKind.WholeNumber),
                        new ExternalProgramResultMapping(
                            "$.isAppContainer",
                            "sandbox.appContainer",
                            ProductionContextValueKind.Boolean)
                    ],
                    new ExternalProgramOutcomeMapping(
                        "$.outcome",
                        "Passed",
                        "Failed",
                        "Aborted"),
                    new ExternalProgramPermissionProfile(
                        "Restricted",
                        NetworkAccessAllowed: false,
                        ["SystemRoot", "WINDIR"]),
                    new ExternalProgramExecutionLimits(
                        VendorDelayMilliseconds + 30_000,
                        MaximumProcessCount: 8,
                        MaximumWorkingSetBytes: 512L * 1024 * 1024,
                        MaximumCpuTimeMilliseconds: VendorDelayMilliseconds + 30_000,
                        MaximumStandardOutputBytes: 2 * 1024 * 1024,
                        MaximumStandardErrorBytes: 2 * 1024 * 1024,
                        MaximumArtifactCount: 32,
                        MaximumArtifactBytes: 16L * 1024 * 1024,
                        MaximumTotalArtifactBytes: 64L * 1024 * 1024)),
                uploads,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            foreach (var upload in uploads)
            {
                await upload.Content.DisposeAsync();
            }
        }
    }

    private static ProjectReleaseSourceMetadata CreateVendorMetadata(
        FlowIrCanonicalArtifact flow,
        ExternalProgramResource resource)
    {
        var resourcePath =
            $"{ExternalProgramResourceContract.ResourceDirectoryName}/{resource.ResourceId}";
        var frozenResource = new ProjectReleaseExternalProgramResource(
            resource.ResourceId,
            resource.DisplayName,
            resource.CapabilityId,
            resource.CommandName,
            resource.LaunchKind.ToString(),
            resource.EntryPoint,
            resource.ProviderKind,
            resource.ProviderKey,
            resource.ArgumentTemplates,
            resource.InputMappings.Select(mapping => new ProjectReleaseExternalProgramInputMapping(
                mapping.Source,
                mapping.Target)).ToArray(),
            resource.ResultMappings.Select(mapping => new ProjectReleaseExternalProgramResultMapping(
                mapping.SourcePath,
                mapping.TargetKey,
                mapping.ValueKind.ToString())).ToArray(),
            new ProjectReleaseExternalProgramOutcomeMapping(
                resource.OutcomeMapping.SourcePath,
                resource.OutcomeMapping.PassedToken,
                resource.OutcomeMapping.FailedToken,
                resource.OutcomeMapping.AbortedToken),
            new ProjectReleaseExternalProgramPermissionProfile(
                resource.PermissionProfile.ProfileName,
                resource.PermissionProfile.NetworkAccessAllowed,
                resource.PermissionProfile.AllowedEnvironmentVariables),
            new ProjectReleaseExternalProgramExecutionLimits(
                resource.ExecutionLimits.TimeoutMilliseconds,
                resource.ExecutionLimits.MaximumProcessCount,
                resource.ExecutionLimits.MaximumWorkingSetBytes,
                resource.ExecutionLimits.MaximumCpuTimeMilliseconds,
                resource.ExecutionLimits.MaximumStandardOutputBytes,
                resource.ExecutionLimits.MaximumStandardErrorBytes,
                resource.ExecutionLimits.MaximumArtifactCount,
                resource.ExecutionLimits.MaximumArtifactBytes,
                resource.ExecutionLimits.MaximumTotalArtifactBytes),
            resource.Files.Select(file => new ProjectReleaseExternalProgramFile(
                $"{resourcePath}/{file.RelativePath}",
                file.SizeBytes,
                file.Sha256)).ToArray(),
            resource.ContentSha256,
            resourcePath);
        return new ProjectReleaseSourceMetadata(
            TopologyId,
            ["layout.main"],
            new ProjectReleaseProductionLine(
                LineDefinitionId,
                "RabbitMQ E2E Line",
                TopologyId,
                new ProjectReleaseProductModel(
                    "product.rabbitmq-e2e",
                    "BOARD",
                    "serialNumber"),
                OperationId,
                [new ProjectReleaseOperation(
                    OperationId,
                    "RabbitMQ broker-outage vendor operation",
                    StationSystemId,
                    FlowDefinitionId,
                    "configuration.rabbitmq-e2e",
                    FlowVersionId,
                    flow.SchemaVersion,
                    flow.Sha256,
                    flow.CanonicalJson,
                    [],
                    [new ProjectReleaseOperationResource(
                        "resource.station-rabbitmq-e2e",
                        "Station",
                        StationSystemId,
                        "Fixed",
                        [])],
                    [],
                    [new ProjectReleaseAuthorizedAction(
                        $"{OperationId}:action:1",
                        OperationId,
                        "DeviceCommand",
                        ExternalProgramCapabilityId,
                        ExternalProgramCommandName,
                        "System",
                        StationSystemId,
                        VendorDelayMilliseconds + 30_000,
                        null)])],
                [new ProjectReleaseRouteTransition(
                    "operation-to-completed",
                    OperationId,
                    null,
                    "Completed",
                    "Sequence",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)],
                []),
            [frozenResource],
            [new ProjectReleaseCapabilityBinding(
                ExternalProgramCapabilityId,
                "binding.vendor-helper",
                ProjectReleaseRuntimeProviderKinds.ExternalSystem,
                resource.ResourceId,
                StationSystemId,
                StationSystemId)],
            [new ProjectReleaseTargetReference("System", StationSystemId)],
            [],
            []);
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> CreateAgentEnvironment(
        StagedPrerequisites prerequisites,
        string root,
        string dataRoot,
        string distributionRoot,
        string packageCacheRoot,
        string runtimeWorkRoot,
        PublishedPackage package,
        string safetyExecutable,
        string agentId,
        string stationId,
        string suffix,
        string restrictedExternalProgramHostSid,
        Uri coordinatorBaseUri,
        string artifactUploadBearerToken)
    {
        var environment = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        Set("AgentId", agentId);
        Set("StationId", stationId);
        Set("StationSystemId", StationSystemId);
        Set("HeartbeatInterval", "00:00:01");
        Set("DataDirectory", dataRoot);
        Set("BrokerUri", prerequisites.BrokerUri.AbsoluteUri);
        Set("RequireBrokerTls", IsTls(prerequisites.BrokerUri).ToString());
        Set("PrefetchCount", "1");
        Set("MaximumConcurrentJobs", "1");
        Set("PackageDistributionDirectory", distributionRoot);
        Set("PackageCacheDirectory", packageCacheRoot);
        Set("MaterialArrivalPackageContentSha256", package.PackageContentSha256);
        Set("MaterialArrivalPipeName", $"olo-rabbitmq-e2e-{suffix}");
        Set($"TrustedPackagePublicKeyFiles__{SigningKeyId}", package.PublicKeyPath);
        Set("RuntimeExecutablePath", "OpenLineOps.StationRuntime.exe");
        Set("PluginHostExecutablePath", "OpenLineOps.PluginHost.exe");
        Set("PythonScript__WorkerExecutablePath", "OpenLineOps.ScriptWorker.exe");
        Set("PythonScript__HostPythonRuntimeDllPath", RequiredHostPythonRuntimeDllPath());
        Set("PythonScript__Sandbox__RequireLeastPrivilegeExecution", "true");
        Set("PythonScript__Sandbox__IsolationMode", "LeastPrivilegeIdentity");
        Set("PythonScript__Sandbox__LeastPrivilegeIdentity", "PerExecutionAppContainer");
        Set(
            "PythonScript__Sandbox__LeastPrivilegeLauncherExecutable",
            "OpenLineOps.LeastPrivilegeLauncher.exe");
        Set("PythonScript__Sandbox__LeastPrivilegeNoInteractivePrompt", "true");
        Set("PythonScript__Sandbox__LeastPrivilegeArgumentsTemplate", string.Empty);
        Set("RuntimeWorkingDirectory", runtimeWorkRoot);
        Set("ArtifactDirectory", Path.Combine(root, "artifacts"));
        Set("CoordinatorBaseUri", coordinatorBaseUri.AbsoluteUri);
        Set("ArtifactUploadBearerToken", artifactUploadBearerToken);
        Set("ArtifactUploadTimeout", "00:01:00");
        Set("RuntimeTimeout", "00:00:30");
        Set("MaximumRuntimeOutputBytes", (2 * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        Set(
            "AllowedRestrictedExternalProgramHostSids__0",
            restrictedExternalProgramHostSid);
        Set("ExternalProgramAppContainerProfileNamespace", $"OpenLineOps.StagedRmq.{suffix}");
        Set("SafetyExecutablePath", safetyExecutable);
        Set("SafetyWorkingDirectory", Path.Combine(root, "safety-work"));
        Set("SafetyTimeout", "00:00:05");
        environment["Logging__LogLevel__Default"] = "Information";
        environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        return environment;

        void Set(string key, string value) =>
            environment[$"OpenLineOps__Agent__{key}"] = value;
    }

    private static StationJobRequested CreateRequest(
        string agentId,
        string stationId,
        string packageContentSha256,
        string suffix)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid();
        return new StationJobRequested(
            Guid.NewGuid(),
            jobId,
            $"staged-rabbitmq-e2e/{suffix}/{jobId:N}",
            agentId,
            stationId,
            StationSystemId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"{OperationId}@0001",
            1,
            "product.rabbitmq-e2e",
            "serialNumber",
            "Delay",
            "lot.rabbitmq-e2e",
            "carrier.rabbitmq-e2e",
            "project.rabbitmq-e2e",
            ApplicationId,
            "snapshot.rabbitmq-e2e",
            LineDefinitionId,
            TopologyId,
            "operator.rabbitmq-e2e",
            packageContentSha256,
            OperationId,
            FlowDefinitionId,
            FlowVersionId,
            "configuration.rabbitmq-e2e",
            "recipe.rabbitmq-e2e@1",
            [new StationResourceFence(
                "Station",
                StationSystemId,
                1,
                nowUtc.AddMinutes(10))],
            JsonSerializer.SerializeToElement(new
            {
                source = new
                {
                    kind = "Text",
                    value = "staged-rabbitmq-e2e"
                }
            }),
            nowUtc);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<StationJobCompleted> WaitForCompletionAsync(
        InMemoryStationJobCoordinationStore store,
        StationJobRequested request,
        WindowsAgentProcess agent,
        TimeSpan timeout)
    {
        StationJobCompleted? completion = null;
        await WaitUntilAsync(
            async () =>
            {
                completion = await store.GetCompletionAsync(request.IdempotencyKey);
                return completion is not null;
            },
            agent,
            timeout,
            "The staged Agent did not publish a terminal StationJobCompleted message.");
        return completion!;
    }

    private static void AssertCompletion(
        StationJobRequested request,
        StationJobCompleted completion)
    {
        Assert.Equal(request.JobId, completion.JobId);
        Assert.Equal(request.IdempotencyKey, completion.IdempotencyKey);
        Assert.Equal(request.RuntimeSessionId, completion.RuntimeSessionId);
        Assert.Equal(ExecutionStatus.Completed, completion.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, completion.Judgement);
        Assert.Null(completion.FailureCode);
        Assert.Null(completion.FailureReason);
        var command = Assert.Single(completion.Commands);
        Assert.Equal(ExecutionStatus.Completed, command.ExecutionStatus);
        Assert.Equal(ExternalProgramCapabilityId, command.CapabilityId);
        Assert.Equal(ExternalProgramCommandName, command.CommandName);
        Assert.Equal(ResultJudgement.Passed, command.ResultJudgement);
        Assert.NotNull(command.ResultPayload);
        Assert.Contains(completion.Artifacts, artifact => artifact.Name == "stdout.log");
        Assert.Contains(completion.Artifacts, artifact => artifact.Name == "stderr.log");
        Assert.Contains(completion.Artifacts, artifact => artifact.Name == "measurements.csv");
        Assert.Contains(completion.Artifacts, artifact => artifact.Name == "inspection.png");
        Assert.Contains(completion.Artifacts, artifact => artifact.Name == "report.pdf");
    }

    private static async Task VerifyCentralArtifactsAsync(
        HttpClient client,
        StationJobCompleted completion)
    {
        await new RemoteTraceStationArtifactReceiptVerifier(client).VerifyAsync(completion);
        foreach (var artifact in completion.Artifacts)
        {
            using var response = await client.GetAsync(
                $"/api/traceability/artifacts/{artifact.StorageKey}");
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(artifact.SizeBytes, bytes.LongLength);
            Assert.Equal(
                artifact.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    private static async Task<IReadOnlyCollection<string>>
        ReadAllPendingOutboxKindsAsync(string sqlitePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind
            FROM station_job_outbox
            WHERE acknowledged_at_utc IS NULL
            ORDER BY job_id, sequence, message_id;
            """;
        var kinds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            kinds.Add(reader.GetString(0));
        }

        return kinds;
    }

    private static async Task AssertNoDuplicateAsync(
        SqliteStationJobStore stationStore,
        InMemoryStationJobCoordinationStore coordinationStore,
        StationJobRequested request,
        long expectedRevision,
        int expectedEventCount,
        int expectedExecutionCount)
    {
        var persisted = await stationStore.GetByIdempotencyKeyAsync(request.IdempotencyKey)
                        ?? throw new InvalidDataException(
                            "The staged Agent lost its durable Station Job after redelivery.");
        Assert.Equal(expectedRevision, persisted.Revision);
        Assert.Equal(StationJobStatus.Completed, persisted.Job.Status);
        Assert.Equal(
            expectedEventCount,
            (await coordinationStore.ListEventsAsync(request.JobId)).Count);
        Assert.Equal(
            expectedExecutionCount,
            CountRuntimeFinishedExecutions(
                await coordinationStore.ListEventsAsync(request.JobId)));
        Assert.Empty(await stationStore.ListPendingOutboxAsync(
            100,
            DateTimeOffset.MaxValue));
    }

    private static int CountRuntimeFinishedExecutions(
        IReadOnlyCollection<StationJobEventInboxItem> events) => events.Count(item =>
    {
        if (!string.Equals(item.Kind, nameof(StationJobProgressed), StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(
            ReadProgressPhase(item),
            "runtime-finished",
            StringComparison.Ordinal);
    });

    private static string ReadProgressPhase(StationJobEventInboxItem item)
    {
        using var payload = JsonDocument.Parse(item.PayloadJson);
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("phase", out var phase)
            || phase.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(phase.GetString()))
        {
            throw new InvalidDataException(
                "Persisted staged Agent progress event has no canonical phase.");
        }

        return phase.GetString()!;
    }

    private static async ValueTask<ProductionLineStationState> ReadPresenceStationAsync(
        ProductionLineRuntimeStateReader reader)
    {
        var state = await reader.ReadAsync(LineDefinitionId);
        return Assert.Single(
            state.Stations,
            station => string.Equals(
                station.StationSystemId,
                StationSystemId,
                StringComparison.Ordinal));
    }

    private static async Task WaitForAcceptedStoppingAsync(
        EvidenceAgentPresenceRepository repository,
        Guid sessionId,
        TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (repository.AcceptedSnapshots.Any(presence =>
                    presence.SessionId == sessionId
                    && presence.State == AgentPresenceState.Stopping))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"The staged Agent session {sessionId:D} did not persist its terminal Stopping presence.");
    }

    private static void AssertPresenceSession(
        EvidenceAgentPresenceRepository repository,
        Guid sessionId)
    {
        var snapshots = repository.AcceptedSnapshots
            .Where(presence => presence.SessionId == sessionId)
            .ToArray();
        var started = Assert.Single(
            snapshots,
            static presence => presence.State == AgentPresenceState.Started);
        Assert.Equal(1, started.Sequence);
        Assert.Contains(
            snapshots,
            static presence => presence.State == AgentPresenceState.Heartbeat);
        var stopping = Assert.Single(
            snapshots,
            static presence => presence.State == AgentPresenceState.Stopping);
        Assert.Equal(stopping, snapshots[^1]);
        Assert.Equal(snapshots.Max(static presence => presence.Sequence), stopping.Sequence);
        for (var index = 1; index < snapshots.Length; index++)
        {
            Assert.True(snapshots[index].Sequence > snapshots[index - 1].Sequence);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        WindowsAgentProcess agent,
        TimeSpan timeout,
        string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (agent.HasExited)
            {
                throw new InvalidOperationException(
                    $"{failure} Agent exited early with code {agent.ExitCode}.");
            }

            if (await predicate())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(failure);
    }

    private static string PrepareSafetyNoOp(string root)
    {
        var sourceRoot = Path.Combine(
            RepositoryRoot(),
            "tools",
            "OpenLineOps.VendorTestHelper",
            "bin",
            BuildConfiguration(),
            "net10.0");
        var sourceExecutable = RequiredDirectFile(
            sourceRoot,
            "OpenLineOps.VendorTestHelper.exe");
        var safetyRoot = Path.Combine(root, "safety-actuator");
        Directory.CreateDirectory(safetyRoot);
        foreach (var fileName in new[]
                 {
                     "OpenLineOps.VendorTestHelper.dll",
                     "OpenLineOps.VendorTestHelper.deps.json",
                     "OpenLineOps.VendorTestHelper.runtimeconfig.json"
                 })
        {
            File.Copy(
                RequiredDirectFile(sourceRoot, fileName),
                Path.Combine(safetyRoot, fileName));
        }

        var safetyExecutable = Path.Combine(safetyRoot, "OpenLineOps.SafetyNoOp.exe");
        File.Copy(sourceExecutable, safetyExecutable);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = safetyExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = safetyRoot,
            ArgumentList = { "emergency-stop", "staged-e2e-probe" }
        }) ?? throw new InvalidOperationException("Safety no-op validation process did not start.");
        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "The independent staged E2E safety no-op actuator failed its startup probe.");
        }

        return safetyExecutable;
    }

    private static string RequiredHostPythonRuntimeDllPath()
    {
        var configured = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "-c \"import pathlib,sysconfig; print(pathlib.Path(sysconfig.get_config_var('BINDIR')).joinpath(sysconfig.get_config_var('LDLIBRARY')).resolve())\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Python runtime discovery process did not start.");
        if (!process.WaitForExit(5_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Staged Agent RabbitMQ E2E could not discover the Python runtime DLL.");
        }

        var discovered = process.StandardOutput.ReadLine();
        return !string.IsNullOrWhiteSpace(discovered) && File.Exists(discovered)
            ? Path.GetFullPath(discovered)
            : throw new InvalidOperationException(
                "Staged Agent RabbitMQ E2E requires an installed Python runtime DLL.");
    }

    [SupportedOSPlatform("windows")]
    private static void WriteEvidence(
        StagedPrerequisites prerequisites,
        PublishedPackage package,
        StationJobRequested request,
        StationJobCompleted completion,
        string sqlitePath,
        IReadOnlyCollection<StationJobEventInboxItem> events,
        int firstAgentPid,
        int restartedAgentPid,
        int firstExitCode,
        int restartExitCode,
        long revision,
        int runtimeFinishedExecutionCount,
        int offlinePendingOutboxCount,
        IReadOnlyCollection<AgentPresenceSnapshot> acceptedPresences,
        ProductionLineStationState onlineBeforeOutage,
        ProductionLineStationState offlineDuringOutage,
        ProductionLineStationState onlineAfterReconnect,
        string outageControlMode,
        bool coordinatorTransportResultInboxRestartedAfterBrokerRecovery,
        RestrictedAgentIdentity agentIdentity,
        string windowsServiceName,
        bool windowsServiceLifecycleVerified,
        AgentHostTokenEvidence initialAgentTokenEvidence,
        AgentHostTokenEvidence restartedAgentTokenEvidence)
    {
        var evidencePath = Environment.GetEnvironmentVariable(EvidencePathVariable);
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(
                new
                {
                    schema = "openlineops.staged-agent-rabbitmq-e2e-evidence",
                    schemaVersion = 1,
                    verifiedAtUtc = DateTimeOffset.UtcNow,
                    agentBundleRoot = prerequisites.AgentBundleRoot,
                    apiBundleRoot = prerequisites.ApiBundleRoot,
                    samplePluginRoot = prerequisites.SamplePluginRoot,
                    agentExecutable = Path.Combine(
                        prerequisites.AgentBundleRoot,
                        "OpenLineOps.Agent.exe"),
                    broker = new
                    {
                        prerequisites.BrokerUri.Host,
                        prerequisites.BrokerUri.Port,
                        tls = IsTls(prerequisites.BrokerUri)
                    },
                    packageContentSha256 = package.PackageContentSha256,
                    packagePath = package.PackagePath,
                    request.JobId,
                    request.IdempotencyKey,
                    request.AgentId,
                    request.StationId,
                    executionStatus = completion.ExecutionStatus.ToString(),
                    judgement = completion.Judgement.ToString(),
                    eventKinds = events.Select(item => item.Kind).ToArray(),
                    progressPhases = events
                        .Where(item => item.Kind == nameof(StationJobProgressed))
                        .Select(ReadProgressPhase)
                        .ToArray(),
                    sqlitePath,
                    firstAgentPid,
                    restartedAgentPid,
                    firstExitCode,
                    restartExitCode,
                    stationJobRevision = revision,
                    runtimeFinishedExecutionCount,
                    vendorProgram = "OpenLineOps.VendorTestHelper.exe",
                    vendorArtifacts = completion.Artifacts
                        .Select(artifact => new
                        {
                            artifact.Name,
                            artifact.Kind,
                            artifact.StorageKey,
                            artifact.ReceiptId,
                            artifact.SizeBytes,
                            artifact.Sha256
                        })
                        .ToArray(),
                    centralArtifactTransport = "authenticated-http-stream",
                    operatorTraceGetVerified = true,
                    brokerOutageVerified = true,
                    outageControlMode,
                    coordinatorTransportResultInboxRestartedAfterBrokerRecovery,
                    windowsServiceName,
                    windowsServiceLifecycleVerified,
                    agentHostIdentity = TokenProjection(
                        initialAgentTokenEvidence,
                        agentIdentity),
                    restartedAgentHostIdentity = TokenProjection(
                        restartedAgentTokenEvidence,
                        agentIdentity),
                    offlinePendingOutboxCount,
                    offlineCompletionWasNotDelivered = true,
                    completionDeliveredOnceAfterReconnect = true,
                    duplicateRedeliveryRejected = true,
                    duplicateAfterRestartRejected = true,
                    presence = new
                    {
                        persistedStates = acceptedPresences
                            .Select(static presence => presence.State.ToString())
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        startedAndHeartbeatPersisted = acceptedPresences.Any(
                            static presence => presence.State == AgentPresenceState.Started)
                            && acceptedPresences.Any(
                                static presence => presence.State
                                    == AgentPresenceState.Heartbeat),
                        accepted = acceptedPresences.Select(static presence => new
                        {
                            presence.AgentId,
                            presence.StationId,
                            presence.StationSystemId,
                            presence.SessionId,
                            presence.Sequence,
                            state = presence.State.ToString(),
                            presence.ObservedAtUtc,
                            presence.ReceivedAtUtc
                        }).ToArray(),
                        onlineBeforeOutage = PresenceProjection(onlineBeforeOutage),
                        offlineDuringBrokerOutage = PresenceProjection(offlineDuringOutage),
                        onlineAfterReconnect = PresenceProjection(onlineAfterReconnect),
                        expiredOfflineDuringBrokerOutage =
                            offlineDuringOutage.Status
                                == ProductionLineStationRuntimeStatus.Offline
                            && offlineDuringOutage.AgentPresenceHealth
                                == ProductionLineAgentPresenceHealth.Expired,
                        freshOnlineAfterReconnect =
                            onlineAfterReconnect.Status
                                == ProductionLineStationRuntimeStatus.Idle
                            && onlineAfterReconnect.AgentPresenceHealth
                                == ProductionLineAgentPresenceHealth.Online
                            && onlineAfterReconnect.AgentPresenceLastSeenAtUtc
                                > offlineDuringOutage.AgentPresenceLastSeenAtUtc
                    },
                    cleanShutdownVerified = firstExitCode == 0 && restartExitCode == 0
                },
                EvidenceJsonOptions));
    }

    [SupportedOSPlatform("windows")]
    private static object TokenProjection(
        AgentHostTokenEvidence evidence,
        RestrictedAgentIdentity requestedIdentity) => new
        {
            requestedAccountName = requestedIdentity.AccountName,
            requestedSid = requestedIdentity.Sid,
            evidence.AccountName,
            evidence.UserSid,
            evidence.IsPrimaryToken,
            evidence.IsElevated,
            evidence.HasRestrictions,
            evidence.AdministratorGroupPresent,
            evidence.AdministratorGroupEnabled,
            evidence.AdministratorGroupDenyOnly,
            evidence.PrincipalAdministratorMembership,
            evidence.IsAuthenticated,
            evidence.IsSystem,
            evidence.NonAdministrative,
            identityStrategy = requestedIdentity.Strategy
        };

    private static object PresenceProjection(ProductionLineStationState station) => new
    {
        status = station.Status.ToString(),
        health = station.AgentPresenceHealth.ToString(),
        station.AgentId,
        station.StationId,
        station.AgentPresenceSessionId,
        station.AgentPresenceSequence,
        state = station.AgentPresenceState?.ToString(),
        station.AgentPresenceLastSeenAtUtc,
        ageMilliseconds = station.AgentPresenceAge?.TotalMilliseconds
    };

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void CaptureCleanupFailure(
        List<Exception> failures,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task CaptureCleanupFailureAsync(
        List<Exception> failures,
        Func<Task> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void DeleteWorkRoot(string root, string packageCacheRoot)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        if (Directory.Exists(packageCacheRoot))
        {
            var protector = new ImmutableContentProtector();
            foreach (var contentDirectory in Directory.EnumerateDirectories(packageCacheRoot)
                         .ToArray())
            {
                var leaf = Path.GetFileName(contentDirectory);
                if (leaf.Length == 64
                    && leaf.All(character =>
                        character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                {
                    protector.DeleteProtectedInstallation(
                        packageCacheRoot,
                        contentDirectory);
                }
            }

            Directory.Delete(packageCacheRoot);
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(root, File.GetAttributes(root) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: true);
    }

    private static string RequiredDirectory(string? value, string variableName)
    {
        var text = RequiredText(value, variableName);
        if (!Path.IsPathFullyQualified(text))
        {
            throw new InvalidDataException(
                $"{variableName} must be a canonical absolute directory path.");
        }

        var path = Path.GetFullPath(text);
        return Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException(
                $"Required staged directory does not exist: {path}");
    }

    private static string RequiredText(string? value, string variableName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"{variableName} must be canonical non-empty text.")
            : value;

    private static string RequiredDirectFile(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A staged E2E dependency must be one canonical direct-child file name.");
        }

        var normalizedRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(fileName, normalizedRoot);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(path), normalizedRoot, comparison))
        {
            throw new InvalidDataException(
                $"Staged E2E file '{fileName}' resolves outside '{normalizedRoot}'.");
        }

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"Required staged E2E file does not exist: {path}",
                path);
    }

    private static bool IsTls(Uri brokerUri) =>
        string.Equals(brokerUri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException(
                   "OpenLineOps repository root could not be found.");
    }

    private static string BuildConfiguration() => AppContext.BaseDirectory.Contains(
        $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase)
        ? "Release"
            : "Debug";

    private static string VendorHelperOutputDirectory() => Path.Combine(
        RepositoryRoot(),
        "tools",
        "OpenLineOps.VendorTestHelper",
        "bin",
        BuildConfiguration(),
        "net10.0");

    private static void Write(
        ProjectApplicationWorkspaceScope scope,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            scope.ApplicationRootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ResourceFileName(string kind, string resourceId)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(resourceId)))[..12];
        return $"{kind}-{resourceId}--{hash}.json";
    }

    private sealed record StagedPrerequisites(
        string AgentBundleRoot,
        string SamplePluginRoot,
        string ApiBundleRoot,
        Uri BrokerUri);

    private sealed record AgentServiceCleanupContract(
        string ManifestPath,
        string Kind,
        string Scope,
        IReadOnlyList<AgentServiceCleanupEntry> Entries);

    private sealed record AgentServiceCleanupEntry(
        string Role,
        string ServiceSuffix,
        string AccountSuffix,
        string ServiceName,
        string AccountName,
        string? AccountSid,
        string ExecutablePath,
        string ExecutableSha256,
        string OwnedRoot);

    private sealed record ArtifactApiCredentials(
        string AgentId,
        string StationId,
        string AgentToken,
        string OperatorToken,
        string SafetyToken)
    {
        public static ArtifactApiCredentials Create(
            string suffix,
            string agentId,
            string stationId) => new(
            agentId,
            stationId,
            Token($"agent:{suffix}"),
            Token($"operator:{suffix}"),
            Token($"safety:{suffix}"));

        private static string Token(string purpose) => Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"OpenLineOps.StagedAgentRabbitMq:{purpose}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class RemoteTraceStationArtifactReceiptVerifier(HttpClient client)
        : IStationArtifactReceiptVerifier
    {
        public async ValueTask VerifyAsync(
            StationJobCompleted completion,
            CancellationToken cancellationToken = default)
        {
            StationMessageContract.Validate(completion);
            foreach (var artifact in completion.Artifacts)
            {
                var receiptKey = StationArtifactReceiptIdentity.ReceiptStorageKey(
                    artifact.ReceiptId);
                using var receiptResponse = await client.GetAsync(
                    $"/api/traceability/artifacts/{receiptKey}",
                    cancellationToken);
                if (receiptResponse.StatusCode != HttpStatusCode.OK)
                {
                    throw new StationArtifactReceiptRejectedException(
                        $"Central receipt GET returned HTTP {(int)receiptResponse.StatusCode}.");
                }

                StationArtifactReceipt receipt;
                try
                {
                    await using var receiptStream = await receiptResponse.Content
                        .ReadAsStreamAsync(cancellationToken);
                    receipt = await JsonSerializer.DeserializeAsync<StationArtifactReceipt>(
                            receiptStream,
                            ReceiptJsonOptions,
                            cancellationToken)
                        ?? throw new InvalidDataException("Central receipt is empty.");
                    StationArtifactReceiptIdentity.Validate(receipt);
                }
                catch (Exception exception) when (exception is JsonException
                                                    or InvalidDataException
                                                    or ArgumentException)
                {
                    throw new StationArtifactReceiptRejectedException(
                        "Central receipt is invalid.",
                        exception);
                }

                var expected = StationArtifactReceiptIdentity.Create(
                    completion.AgentId,
                    completion.StationId,
                    completion.JobId,
                    artifact.Name,
                    artifact.Kind,
                    artifact.MediaType,
                    artifact.SizeBytes,
                    artifact.Sha256);
                if (!Equals(receipt, expected)
                    || !string.Equals(
                        artifact.StorageKey,
                        receipt.StorageKey,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        artifact.ReceiptId,
                        receipt.ReceiptId,
                        StringComparison.Ordinal))
                {
                    throw new StationArtifactReceiptRejectedException(
                        "Central receipt does not match Station completion evidence.");
                }

                using var artifactResponse = await client.GetAsync(
                    $"/api/traceability/artifacts/{artifact.StorageKey}",
                    cancellationToken);
                if (artifactResponse.StatusCode != HttpStatusCode.OK)
                {
                    throw new StationArtifactReceiptRejectedException(
                        $"Central artifact GET returned HTTP {(int)artifactResponse.StatusCode}.");
                }

                var content = await artifactResponse.Content
                    .ReadAsByteArrayAsync(cancellationToken);
                if (content.LongLength != artifact.SizeBytes
                    || !string.Equals(
                        Convert.ToHexStringLower(SHA256.HashData(content)),
                        artifact.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new StationArtifactReceiptRejectedException(
                        "Central artifact bytes do not match Station completion evidence.");
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed partial class StagedCoordinatorApiProcess : IAsyncDisposable
    {
        private readonly WebApplicationFactory<CoordinatorApiProgram> _factory;

        private StagedCoordinatorApiProcess(
            WebApplicationFactory<CoordinatorApiProgram> factory,
            Uri baseUri)
        {
            _factory = factory;
            BaseUri = baseUri;
        }

        public Uri BaseUri { get; }

        public IStationJobCoordinationStore CoordinationStore =>
            _factory.Services.GetRequiredService<IStationJobCoordinationStore>();

        public static async Task<StagedCoordinatorApiProcess> StartAsync(
            string apiBundleRoot,
            string workRoot,
            ArtifactApiCredentials credentials,
            TimeSpan timeout)
        {
            _ = RequiredDirectFile(apiBundleRoot, "OpenLineOps.Api.exe");
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["OpenLineOps:Runtime:Persistence:Provider"] = "InMemory",
                ["OpenLineOps:Runtime:Coordination:Provider"] = "InMemory",
                ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess",
                ["OpenLineOps:EventBus:UseInMemory"] = "true",
                ["OpenLineOps:Traceability:Persistence:Provider"] = "Sqlite",
                ["OpenLineOps:Traceability:Persistence:DatabasePath"] =
                    Path.Combine(workRoot, "api-trace.sqlite"),
                ["OpenLineOps:Traceability:ArtifactStorage:Provider"] = "FileSystem",
                ["OpenLineOps:Traceability:ArtifactStorage:RootPath"] =
                    Path.Combine(workRoot, "api-trace-artifacts"),
                ["OpenLineOps:Traceability:ArtifactUpload:MaximumArtifactSizeBytes"] =
                    "16777216",
                ["OpenLineOps:Processes:Persistence:Provider"] = "InMemory",
                ["OpenLineOps:Devices:Persistence:Provider"] = "Sqlite",
                ["OpenLineOps:Devices:Persistence:DatabasePath"] =
                    Path.Combine(workRoot, "api-devices.sqlite"),
                ["OpenLineOps:Operations:Persistence:Provider"] = "Sqlite",
                ["OpenLineOps:Operations:Persistence:DatabasePath"] =
                    Path.Combine(workRoot, "api-operations.sqlite"),
                ["OpenLineOps:Plugins:EventLog:Provider"] = "Sqlite",
                ["OpenLineOps:Plugins:EventLog:DatabasePath"] =
                    Path.Combine(workRoot, "api-plugin-events.sqlite")
            };
            SetCaller(
                settings,
                0,
                "staged-safety",
                "staged.safety",
                credentials.SafetyToken,
                OpenLineOps.Api.Abstractions.OpenLineOpsApiSecurity.SafetyRole);
            SetCaller(
                settings,
                1,
                "staged-operator",
                "staged.operator",
                credentials.OperatorToken,
                OpenLineOps.Api.Abstractions.OpenLineOpsApiSecurity.OperatorRole);
            SetCaller(
                settings,
                2,
                "staged-agent",
                credentials.AgentId,
                credentials.AgentToken,
                OpenLineOps.Api.Abstractions.OpenLineOpsApiSecurity.StationAgentRole,
                credentials.StationId);

            var factory = new WebApplicationFactory<CoordinatorApiProgram>()
                .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
                    (_, configuration) => configuration.AddInMemoryCollection(settings)));
            factory.UseKestrel(0);
            var probe = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var addresses = factory.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()?
                .Addresses
                .Select(static address => new Uri(address, UriKind.Absolute))
                .Where(static address => address.IsLoopback
                                         && address.Scheme == Uri.UriSchemeHttp
                                         && address.Port > 0)
                .ToArray() ?? [];
            if (addresses.Length != 1)
            {
                probe.Dispose();
                await factory.DisposeAsync();
                throw new InvalidOperationException(
                    "Staged Coordinator API must expose exactly one dynamic loopback HTTP address.");
            }

            probe.BaseAddress = addresses[0];
            var hosted = new StagedCoordinatorApiProcess(factory, addresses[0]);
            probe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                credentials.OperatorToken);
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                using var response = await probe.GetAsync("/health/ready");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    probe.Dispose();
                    return hosted;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            await hosted.DisposeAsync();
            throw new TimeoutException("Staged Coordinator API readiness timed out.");
        }

        private static void SetCaller(
            Dictionary<string, string?> settings,
            int index,
            string credentialId,
            string actorId,
            string token,
            string role,
            string? stationId = null)
        {
            var prefix = $"OpenLineOps:Security:Callers:{index}";
            settings[$"{prefix}:CredentialId"] = credentialId;
            settings[$"{prefix}:ActorId"] = actorId;
            settings[$"{prefix}:TokenSha256"] = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            settings[$"{prefix}:Roles:0"] = role;
            if (stationId is not null)
            {
                settings[$"{prefix}:StationId"] = stationId;
            }
        }

        public HttpClient CreateAuthenticatedClient(string token)
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            })
            {
                BaseAddress = BaseUri,
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
        }
    }

    private sealed record PublishedPackage(
        string PackageContentSha256,
        string PackagePath,
        string PublicKeyPath);

    private sealed record AgentHostTokenEvidence(
        string AccountName,
        string UserSid,
        bool IsPrimaryToken,
        bool IsElevated,
        bool HasRestrictions,
        bool AdministratorGroupPresent,
        bool AdministratorGroupEnabled,
        bool AdministratorGroupDenyOnly,
        bool PrincipalAdministratorMembership,
        bool IsAuthenticated,
        bool IsSystem)
    {
        public bool NonAdministrative =>
            IsPrimaryToken
            && !IsElevated
            && !AdministratorGroupPresent
            && !AdministratorGroupEnabled
            && !AdministratorGroupDenyOnly
            && !PrincipalAdministratorMembership
            && IsAuthenticated
            && !IsSystem;
    }

    private sealed class EvidenceAgentPresenceRepository : IAgentPresenceRepository
    {
        private readonly InMemoryAgentPresenceRepository _inner = new();
        private readonly ConcurrentQueue<AgentPresenceSnapshot> _accepted = new();

        public IReadOnlyCollection<AgentPresenceSnapshot> AcceptedSnapshots =>
            _accepted.ToArray();

        public IReadOnlyCollection<AgentPresenceState> AcceptedStates => _accepted
            .Select(static presence => presence.State)
            .Distinct()
            .ToArray();

        public async ValueTask<bool> RecordAsync(
            AgentPresenceReported presence,
            DateTimeOffset receivedAtUtc,
            CancellationToken cancellationToken = default)
        {
            var accepted = await _inner.RecordAsync(
                presence,
                receivedAtUtc,
                cancellationToken);
            if (accepted)
            {
                var persisted = await _inner.GetAsync(
                    presence.AgentId,
                    presence.StationId,
                    cancellationToken);
                _accepted.Enqueue(Assert.IsType<AgentPresenceSnapshot>(persisted));
            }

            return accepted;
        }

        public ValueTask<AgentPresenceSnapshot?> GetAsync(
            string agentId,
            string stationId,
            CancellationToken cancellationToken = default) =>
            _inner.GetAsync(agentId, stationId, cancellationToken);

        public ValueTask<IReadOnlyCollection<AgentPresenceSnapshot>> ListAsync(
            CancellationToken cancellationToken = default) =>
            _inner.ListAsync(cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private sealed class RestrictedAgentIdentity : IDisposable
    {
        private const uint UserPrivilegeUser = 1;
        private const uint UserFlagScript = 0x0001;
        private const uint UserFlagNormalAccount = 0x0200;
        private const uint UserFlagPasswordDoesNotExpire = 0x10000;
        private const uint Success = 0;
        private const uint AccessDenied = 5;
        private const uint UserNotFound = 2221;
        private const uint MemberAlreadyInAlias = 1378;
        private const uint PolicyCreateAccount = 0x00000010;
        private const uint PolicyLookupNames = 0x00000800;
        private const uint StatusObjectNameNotFound = 0xC0000034;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorAccessDenied = 5;
        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;
        private const int ErrorBusy = 170;
        private const int ErrorUserMappedFile = 1224;
        private const uint InvalidFileAttributes = uint.MaxValue;
        private const string ServiceLogonRight = "SeServiceLogonRight";
        private const string TemporaryStandardServiceAccountStrategy =
            "temporary-standard-service-account";
        private const string TemporaryStandardServiceAccountComment =
            "Temporary OpenLineOps staged Agent E2E identity";
        private static readonly TimeSpan ProfileDeletionTimeout =
            TimeSpan.FromSeconds(60);

        private readonly List<GrantedDirectoryAccess> _grantedAccess = [];
        private readonly string _userName;
        private readonly string _password;
        private string? _unprovenInstalledServiceName;
        private bool _serviceLogonRightGranted;
        private bool _disposed;

        private RestrictedAgentIdentity(
            string accountName,
            string sid,
            string userName,
            string password)
        {
            AccountName = accountName;
            Sid = sid;
            _userName = userName;
            _password = password;
            Strategy = TemporaryStandardServiceAccountStrategy;
            _serviceLogonRightGranted = true;
        }

        public string AccountName { get; }

        public string Sid { get; }

        public string Strategy { get; }

        internal string UserName => _userName;

        internal string Password => _password;

        internal string ServiceAccountName => $".\\{_userName}";

        internal bool HasUnprovenServiceInstallation =>
            _unprovenInstalledServiceName is not null;

        internal void MarkServiceInstalled(string serviceName)
        {
            if (_unprovenInstalledServiceName is not null)
            {
                throw new InvalidOperationException(
                    $"Staged Agent identity '{AccountName}' is already bound to unproven service '{_unprovenInstalledServiceName}'.");
            }

            _unprovenInstalledServiceName = serviceName;
        }

        internal void MarkServiceDeletionProven(string serviceName)
        {
            if (!string.Equals(
                    _unprovenInstalledServiceName,
                    serviceName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Staged Agent identity '{AccountName}' is not bound to service '{serviceName}'.");
            }

            _unprovenInstalledServiceName = null;
        }

        public static RestrictedAgentIdentity CreateRequired(string suffix)
        {
            if (suffix.Length != 32
                || suffix.Any(static character =>
                    character is not (>= '0' and <= '9')
                        and not (>= 'a' and <= 'f')))
            {
                throw new ArgumentException(
                    "The staged Agent service-account suffix must contain exactly 32 lowercase hexadecimal characters.",
                    nameof(suffix));
            }

            var userName = $"oloe2e{suffix[..10]}";
            var password = $"Aa1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}";
            var user = new UserInfo1
            {
                Name = userName,
                Password = password,
                Privilege = UserPrivilegeUser,
                Flags = UserFlagScript | UserFlagNormalAccount | UserFlagPasswordDoesNotExpire,
                Comment = TemporaryStandardServiceAccountComment
            };
            var status = NetUserAdd(null, 1, ref user, out var parameterError);
            if (status != Success)
            {
                throw new InvalidOperationException(
                    status == AccessDenied
                        ? "The formal staged Agent service gate requires elevated permission to provision its temporary standard Windows service account; NetUserAdd was denied."
                        : $"Could not provision the staged Agent standard service account (NetUserAdd {status}, parameter {parameterError}).");
            }

            SecurityIdentifier? sid = null;
            try
            {
                sid = (SecurityIdentifier)new NTAccount(
                        Environment.MachineName,
                        userName)
                    .Translate(typeof(SecurityIdentifier));
                UpdateCleanupManifestAccountSid(suffix, userName, sid.Value);
                AddToBuiltinUsers(sid);
                GrantServiceLogonRight(sid);
                return new RestrictedAgentIdentity(
                    $"{Environment.MachineName}\\{userName}",
                    sid.Value,
                    userName,
                    password);
            }
            catch (Exception exception)
            {
                var failures = new List<Exception> { exception };
                if (sid is not null)
                {
                    CaptureCleanupFailure(
                        failures,
                        () => RemoveServiceLogonRightIfPresent(sid));
                }

                if (failures.Count == 1)
                {
                    if (sid is null)
                    {
                        failures.Add(new InvalidOperationException(
                            $"Could not bind staged Agent account '{userName}' to its exact SID; preserving it for diagnosis."));
                    }
                    else
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => DeleteExactAccountRequired(userName, sid.Value));
                    }
                }

                if (failures.Count > 1)
                {
                    throw new AggregateException(
                        "Staged Agent service-account creation failed and rollback was incomplete.",
                        failures);
                }

                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }
        }

        public static RunScopedIdentity? ReadRunScopedIdentity(
            string accountName,
            string? recordedSid)
        {
            ValidateRunScopedAccountName(accountName);
            var status = NetUserGetInfo(null, accountName, 1, out var buffer);
            if (status == UserNotFound)
            {
                return recordedSid is null
                    ? null
                    : new RunScopedIdentity(accountName, recordedSid, AccountExists: false);
            }

            if (status != Success)
            {
                throw new InvalidOperationException(
                    $"Could not inspect run-scoped staged Agent account '{accountName}' (NetUserGetInfo {status}).");
            }

            UserInfo1 user;
            uint freeStatus;
            try
            {
                user = Marshal.PtrToStructure<UserInfo1>(buffer);
            }
            finally
            {
                freeStatus = NetApiBufferFree(buffer);
            }

            if (freeStatus != Success)
            {
                throw new InvalidOperationException(
                    $"Could not release NetUserGetInfo memory (NetApiBufferFree {freeStatus}).");
            }

            if (!string.Equals(user.Name, accountName, StringComparison.Ordinal)
                || user.Privilege != UserPrivilegeUser
                || (user.Flags & UserFlagNormalAccount) == 0
                || !string.Equals(
                    user.Comment,
                    TemporaryStandardServiceAccountComment,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Run-scoped account '{accountName}' does not match the exact temporary service-account contract.");
            }

            if (recordedSid is null)
            {
                throw new InvalidOperationException(
                    $"Run-scoped account '{accountName}' exists before its exact SID was committed to the protected cleanup manifest; preserving it for diagnosis.");
            }

            var sid = (SecurityIdentifier)new NTAccount(
                    Environment.MachineName,
                    accountName)
                .Translate(typeof(SecurityIdentifier));
            if (recordedSid is not null
                && !string.Equals(recordedSid, sid.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Run-scoped account '{accountName}' SID differs from its protected cleanup manifest.");
            }

            return new RunScopedIdentity(accountName, sid.Value, AccountExists: true);
        }

        public static void CleanupRunScoped(
            RunScopedIdentity? identity,
            string expectedAccountName,
            string ownedRoot,
            IReadOnlySet<string> allowedRootSids)
        {
            ArgumentNullException.ThrowIfNull(allowedRootSids);
            ValidateRunScopedAccountName(expectedAccountName);
            if (identity is null)
            {
                EnsureAccountAbsent(expectedAccountName);
                return;
            }

            if (!string.Equals(
                    identity.AccountName,
                    expectedAccountName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Run-scoped staged Agent identity differs from its cleanup manifest account.");
            }

            var sid = new SecurityIdentifier(identity.Sid);
            VerifyRunScopedOwnedRootForIdentityCleanup(ownedRoot, allowedRootSids);
            RemoveExactSidAccessRules(ownedRoot, sid);
            RemoveServiceLogonRightIfPresent(sid);
            DeleteProfileRequired(identity.Sid, expectedAccountName);
            DeleteExactAccountRequired(expectedAccountName, identity.Sid);
        }

        public void GrantDirectoryAccess(
            string path,
            FileSystemRights rights,
            InheritanceFlags inheritanceFlags =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"Cannot grant staged Agent access to missing directory '{directory.FullName}'.");
            }

            var rule = new FileSystemAccessRule(
                new SecurityIdentifier(Sid),
                rights,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow);
            var security = FileSystemAclExtensions.GetAccessControl(directory);
            security.AddAccessRule(rule);
            FileSystemAclExtensions.SetAccessControl(directory, security);
            _grantedAccess.Add(new GrantedDirectoryAccess(directory.FullName, rule));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_unprovenInstalledServiceName is not null)
            {
                throw new InvalidOperationException(
                    $"Staged Agent service '{_unprovenInstalledServiceName}' deletion is unproven; preserving its account, SeServiceLogonRight, profile, and ACLs for safe diagnosis.");
            }

            var failures = new List<Exception>();
            foreach (var granted in _grantedAccess.AsEnumerable().Reverse())
            {
                CaptureCleanupFailure(
                    failures,
                    () =>
                    {
                        if (!Directory.Exists(granted.Path))
                        {
                            return;
                        }

                        var directory = new DirectoryInfo(granted.Path);
                        var security = FileSystemAclExtensions.GetAccessControl(directory);
                        security.RemoveAccessRuleSpecific(granted.Rule);
                        FileSystemAclExtensions.SetAccessControl(directory, security);
                    });
            }

            ThrowIdentityCleanupFailures(
                failures,
                "Staged Agent ACL cleanup failed; preserving its SeServiceLogonRight, profile, and account.");
            _grantedAccess.Clear();

            if (_serviceLogonRightGranted)
            {
                RemoveServiceLogonRight(new SecurityIdentifier(Sid));
                _serviceLogonRightGranted = false;
            }

            DeleteProfileRequired(Sid, _userName);
            DeleteExactAccountRequired(_userName, Sid);

            _disposed = true;
        }

        private static void RemoveExactSidAccessRules(
            string ownedRoot,
            SecurityIdentifier sid)
        {
            if (!Directory.Exists(ownedRoot))
            {
                return;
            }

            EnsureNoReparsePointInExistingPath(ownedRoot, "run-scoped staged Agent ACL root");
            var paths = RejectTreeReparsePoints(
                    ownedRoot,
                    "run-scoped staged Agent ACL root")
                .OrderByDescending(static path => path.Length)
                .Append(ownedRoot)
                .ToArray();
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var directory = new DirectoryInfo(path);
                    var security = FileSystemAclExtensions.GetAccessControl(directory);
                    security.PurgeAccessRules(sid);
                    FileSystemAclExtensions.SetAccessControl(directory, security);
                }
                else if (File.Exists(path))
                {
                    var file = new FileInfo(path);
                    var security = FileSystemAclExtensions.GetAccessControl(file);
                    security.PurgeAccessRules(sid);
                    FileSystemAclExtensions.SetAccessControl(file, security);
                }
            }

            foreach (var path in paths)
            {
                FileSystemSecurity security = Directory.Exists(path)
                    ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
                    : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
                var rules = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier));
                if (rules.Cast<FileSystemAccessRule>().Any(rule =>
                        rule.IdentityReference is SecurityIdentifier identity
                        && string.Equals(identity.Value, sid.Value, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Run-scoped staged Agent SID '{sid.Value}' still has an ACL entry on '{path}'.");
                }
            }
        }

        private static void ValidateRunScopedAccountName(string accountName)
        {
            if (accountName.Length != 16
                || !accountName.StartsWith("oloe2e", StringComparison.Ordinal)
                || accountName[6..].Any(static character =>
                    character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw new InvalidDataException(
                    "Run-scoped staged Agent account name is not canonical.");
            }
        }

        private static void EnsureAccountAbsent(string accountName)
        {
            var status = NetUserGetInfo(null, accountName, 1, out var buffer);
            if (status == Success)
            {
                _ = NetApiBufferFree(buffer);
                throw new InvalidOperationException(
                    $"Run-scoped staged Agent account '{accountName}' still exists after cleanup.");
            }

            if (status != UserNotFound)
            {
                throw new InvalidOperationException(
                    $"Could not prove deletion of run-scoped staged Agent account '{accountName}' (NetUserGetInfo {status}).");
            }
        }

        private static void DeleteExactAccountRequired(
            string accountName,
            string expectedSid)
        {
            _ = new SecurityIdentifier(expectedSid);
            var currentIdentity = ReadRunScopedIdentity(accountName, expectedSid)
                                  ?? throw new InvalidOperationException(
                                      $"Could not bind run-scoped staged Agent account '{accountName}' to its exact SID immediately before deletion.");
            if (!currentIdentity.AccountExists)
            {
                EnsureAccountAbsent(accountName);
                return;
            }

            var status = NetUserDel(null, accountName);
            if (status is not Success and not UserNotFound)
            {
                throw new InvalidOperationException(
                    $"Could not delete the exact run-scoped staged Agent account '{accountName}' (NetUserDel {status}).");
            }

            EnsureAccountAbsent(accountName);
        }

        private static void DeleteProfileRequired(string sid, string accountName)
        {
            const string profileListRegistryPath =
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
            var canonicalSid = new SecurityIdentifier(sid).Value;
            if (!string.Equals(canonicalSid, sid, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Run-scoped staged Agent profile SID '{sid}' is not canonical.");
            }

            var profileRegistryPath = profileListRegistryPath + "\\" + sid;
            var userProfilesRoot = ReadRequiredProfilesDirectory(
                profileListRegistryPath);
            var profilesRootAttributes = ReadPathAttributesRequired(
                userProfilesRoot,
                "configured Windows user-profile root")
                ?? throw new DirectoryNotFoundException(
                    $"The configured Windows user-profile root '{userProfilesRoot}' does not exist.");
            if (!profilesRootAttributes.HasFlag(FileAttributes.Directory))
            {
                throw new InvalidDataException(
                    $"The configured Windows user-profile root '{userProfilesRoot}' is not a directory.");
            }

            EnsureNoReparsePointInExistingPath(
                userProfilesRoot,
                "configured Windows user-profile root");
            var expectedDefaultProfilePath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(userProfilesRoot, accountName)));
            if (!string.Equals(
                    Path.GetDirectoryName(expectedDefaultProfilePath),
                    userProfilesRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(expectedDefaultProfilePath),
                    accountName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The expected profile path for account '{accountName}' is not a direct child of the configured profile root.");
            }

            VerifyProfileDeletionProvenance(
                profileListRegistryPath,
                profileRegistryPath,
                userProfilesRoot,
                expectedDefaultProfilePath,
                sid,
                accountName);
            var initialRegisteredProfilePath = ReadValidatedRegisteredProfilePath(
                profileRegistryPath,
                sid);
            var initialState = ReadProfileArtifactState(
                profileRegistryPath,
                expectedDefaultProfilePath,
                sid);
            if (initialRegisteredProfilePath is null)
            {
                if (initialState.Absent)
                {
                    return;
                }

                throw CreateProfileDeletionException(
                    sid,
                    nativeErrorCode: 0,
                    initialState,
                    "Exact profile artifacts exist without a ProfileList SID-to-path binding");
            }

            if (!string.Equals(
                    initialRegisteredProfilePath,
                    expectedDefaultProfilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Run-scoped staged Agent initial ProfileImagePath '{initialRegisteredProfilePath}' is not the exact direct profile for account '{accountName}'.");
            }

            // Never use a mutable registry string as the destructive path argument.
            // The captured binding authorizes only this already-derived direct child.
            var capturedRegisteredProfilePath = expectedDefaultProfilePath;

            var elapsed = Stopwatch.StartNew();
            var lastNativeError = 0;
            while (true)
            {
                VerifyProfileDeletionProvenance(
                    profileListRegistryPath,
                    profileRegistryPath,
                    userProfilesRoot,
                    expectedDefaultProfilePath,
                    sid,
                    accountName);
                var state = ReadProfileArtifactState(
                    profileRegistryPath,
                    expectedDefaultProfilePath,
                    sid);
                if (elapsed.Elapsed >= ProfileDeletionTimeout)
                {
                    throw CreateProfileDeletionException(
                        sid,
                        lastNativeError,
                        state,
                        "The bounded profile-unload and deletion deadline expired");
                }

                if (!state.SidHiveLoaded && !state.ClassesHiveLoaded)
                {
                    if (DeleteProfile(sid, capturedRegisteredProfilePath, null))
                    {
                        VerifyProfileAbsent(
                            profileListRegistryPath,
                            profileRegistryPath,
                            userProfilesRoot,
                            expectedDefaultProfilePath,
                            sid,
                            accountName,
                            elapsed);
                        return;
                    }

                    lastNativeError = Marshal.GetLastWin32Error();
                    state = ReadProfileArtifactState(
                        profileRegistryPath,
                        expectedDefaultProfilePath,
                        sid);
                    if (lastNativeError is ErrorFileNotFound or ErrorPathNotFound)
                    {
                        if (state.Absent)
                        {
                            return;
                        }

                        throw CreateProfileDeletionException(
                            sid,
                            lastNativeError,
                            state,
                            "DeleteProfileW reported an absent path while exact profile residue remained");
                    }

                    if (!IsTransientProfileDeletionError(lastNativeError))
                    {
                        throw CreateProfileDeletionException(
                            sid,
                            lastNativeError,
                            state,
                            "DeleteProfileW returned a non-transient error");
                    }
                }

                Thread.Sleep(GetProfileDeletionRetryDelay(elapsed.Elapsed));
            }
        }

        private static void VerifyProfileAbsent(
            string profileListRegistryPath,
            string profileRegistryPath,
            string userProfilesRoot,
            string expectedDefaultProfilePath,
            string sid,
            string accountName,
            Stopwatch elapsed)
        {
            while (true)
            {
                VerifyProfileDeletionProvenance(
                    profileListRegistryPath,
                    profileRegistryPath,
                    userProfilesRoot,
                    expectedDefaultProfilePath,
                    sid,
                    accountName);
                var state = ReadProfileArtifactState(
                    profileRegistryPath,
                    expectedDefaultProfilePath,
                    sid);
                if (state.Absent)
                {
                    return;
                }

                if (elapsed.Elapsed >= ProfileDeletionTimeout)
                {
                    throw CreateProfileDeletionException(
                        sid,
                        nativeErrorCode: 0,
                        state,
                        "DeleteProfileW succeeded but exact profile residue remained until the bounded deadline");
                }

                Thread.Sleep(GetProfileDeletionRetryDelay(elapsed.Elapsed));
            }
        }

        internal static bool IsTransientProfileDeletionError(int nativeErrorCode) =>
            nativeErrorCode is ErrorAccessDenied
                or ErrorSharingViolation
                or ErrorLockViolation
                or ErrorBusy
                or ErrorUserMappedFile;

        private static TimeSpan GetProfileDeletionRetryDelay(TimeSpan elapsed)
        {
            var remaining = ProfileDeletionTimeout - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var desired = elapsed < TimeSpan.FromSeconds(2)
                ? TimeSpan.FromMilliseconds(100)
                : elapsed < TimeSpan.FromSeconds(10)
                    ? TimeSpan.FromMilliseconds(250)
                    : TimeSpan.FromMilliseconds(500);
            return desired <= remaining ? desired : remaining;
        }

        private static string ReadRequiredProfilesDirectory(
            string profileListRegistryPath)
        {
            string profilesDirectory;
            using (var profileListKey = Registry.LocalMachine.OpenSubKey(
                       profileListRegistryPath,
                       writable: false)
                   ?? throw new InvalidOperationException(
                       "The Windows ProfileList registry key is missing."))
            {
                profilesDirectory = profileListKey.GetValue(
                        "ProfilesDirectory",
                        defaultValue: null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                    ?? throw new InvalidOperationException(
                        "The Windows ProfileList ProfilesDirectory value is missing or is not a string.");
            }

            profilesDirectory = Environment.ExpandEnvironmentVariables(profilesDirectory);
            if (string.IsNullOrWhiteSpace(profilesDirectory)
                || !Path.IsPathFullyQualified(profilesDirectory))
            {
                throw new InvalidDataException(
                    "The Windows ProfileList ProfilesDirectory value is not an absolute path.");
            }

            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(profilesDirectory));
        }

        private static void VerifyProfileDeletionProvenance(
            string profileListRegistryPath,
            string profileRegistryPath,
            string userProfilesRoot,
            string expectedDefaultProfilePath,
            string sid,
            string accountName)
        {
            var currentProfilesRoot = ReadRequiredProfilesDirectory(
                profileListRegistryPath);
            if (!string.Equals(
                    currentProfilesRoot,
                    userProfilesRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The configured Windows user-profile root changed during staged Agent cleanup.");
            }

            var profilesRootAttributes = ReadPathAttributesRequired(
                userProfilesRoot,
                "configured Windows user-profile root")
                ?? throw new DirectoryNotFoundException(
                    $"The configured Windows user-profile root '{userProfilesRoot}' disappeared during staged Agent cleanup.");
            if (!profilesRootAttributes.HasFlag(FileAttributes.Directory))
            {
                throw new InvalidDataException(
                    $"The configured Windows user-profile root '{userProfilesRoot}' is not a directory.");
            }

            EnsureNoReparsePointInExistingPath(
                userProfilesRoot,
                "configured Windows user-profile root");
            var registeredProfilePath = ReadValidatedRegisteredProfilePath(
                profileRegistryPath,
                sid);
            if (registeredProfilePath is not null
                && !string.Equals(
                    registeredProfilePath,
                    expectedDefaultProfilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Run-scoped staged Agent ProfileImagePath '{registeredProfilePath}' is not the exact direct profile for account '{accountName}'.");
            }

            var profileAttributes = ReadPathAttributesRequired(
                expectedDefaultProfilePath,
                "run-scoped staged Agent profile path");
            if (profileAttributes is not null)
            {
                if (!profileAttributes.Value.HasFlag(FileAttributes.Directory))
                {
                    throw new InvalidDataException(
                        $"Run-scoped staged Agent profile path '{expectedDefaultProfilePath}' is not a directory.");
                }

                EnsureNoReparsePointInExistingPath(
                    expectedDefaultProfilePath,
                    "run-scoped staged Agent profile path");
            }
        }

        private static string? ReadValidatedRegisteredProfilePath(
            string profileRegistryPath,
            string sid)
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                profileRegistryPath,
                writable: false);
            if (key is null)
            {
                return null;
            }

            var registeredProfileValue = key.GetValue(
                "ProfileImagePath",
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (registeredProfileValue is not string registeredProfilePath
                || string.IsNullOrWhiteSpace(registeredProfilePath))
            {
                throw new InvalidDataException(
                    $"Run-scoped staged Agent profile for SID '{sid}' has a missing, empty, or non-string ProfileImagePath.");
            }

            registeredProfilePath = Environment.ExpandEnvironmentVariables(
                registeredProfilePath);
            if (!Path.IsPathFullyQualified(registeredProfilePath))
            {
                throw new InvalidDataException(
                    $"Run-scoped staged Agent ProfileImagePath '{registeredProfilePath}' is not absolute.");
            }

            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(registeredProfilePath));
        }

        private static ProfileArtifactState ReadProfileArtifactState(
            string profileRegistryPath,
            string expectedDefaultProfilePath,
            string sid)
        {
            bool profileRegistryPresent;
            using (var key = Registry.LocalMachine.OpenSubKey(
                       profileRegistryPath,
                       writable: false))
            {
                profileRegistryPresent = key is not null;
            }

            var profileAttributes = ReadPathAttributesRequired(
                expectedDefaultProfilePath,
                "run-scoped staged Agent profile path");
            var profilePathPresent = profileAttributes is not null;
            if (profileAttributes is not null)
            {
                if (!profileAttributes.Value.HasFlag(FileAttributes.Directory))
                {
                    throw new InvalidDataException(
                        $"Run-scoped staged Agent profile path '{expectedDefaultProfilePath}' is not a directory.");
                }

                EnsureNoReparsePointInExistingPath(
                    expectedDefaultProfilePath,
                    "run-scoped staged Agent profile path");
            }

            bool sidHiveLoaded;
            using (var key = Registry.Users.OpenSubKey(sid, writable: false))
            {
                sidHiveLoaded = key is not null;
            }

            bool classesHiveLoaded;
            using (var key = Registry.Users.OpenSubKey(
                       sid + "_Classes",
                       writable: false))
            {
                classesHiveLoaded = key is not null;
            }

            return new ProfileArtifactState(
                profileRegistryPresent,
                profilePathPresent,
                sidHiveLoaded,
                classesHiveLoaded);
        }

        private static FileAttributes? ReadPathAttributesRequired(
            string path,
            string purpose)
        {
            var attributes = GetFileAttributes(path);
            if (attributes != InvalidFileAttributes)
            {
                return (FileAttributes)attributes;
            }

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            throw new Win32Exception(
                error,
                $"Could not inspect {purpose} '{path}' while proving staged Agent profile cleanup.");
        }

        private static Win32Exception CreateProfileDeletionException(
            string sid,
            int nativeErrorCode,
            ProfileArtifactState state,
            string reason) => new(
            nativeErrorCode,
            $"Could not delete the temporary staged Agent service profile for SID {sid}; "
            + $"{reason}. NativeErrorCode={nativeErrorCode}; "
            + $"ProfileListPresent={state.ProfileRegistryPresent}; "
            + $"ProfilePathPresent={state.ProfilePathPresent}; "
            + $"HkuSidLoaded={state.SidHiveLoaded}; "
            + $"HkuClassesLoaded={state.ClassesHiveLoaded}. "
            + "The exact account is preserved for bounded cleanup retry.");

        private sealed record ProfileArtifactState(
            bool ProfileRegistryPresent,
            bool ProfilePathPresent,
            bool SidHiveLoaded,
            bool ClassesHiveLoaded)
        {
            public bool Absent =>
                !ProfileRegistryPresent
                && !ProfilePathPresent
                && !SidHiveLoaded
                && !ClassesHiveLoaded;
        }

        private static void ThrowIdentityCleanupFailures(
            List<Exception> failures,
            string message)
        {
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(message, failures);
            }
        }

        private static void AddToBuiltinUsers(SecurityIdentifier userSid)
        {
            var builtinUsers = (NTAccount)new SecurityIdentifier(
                    WellKnownSidType.BuiltinUsersSid,
                    null)
                .Translate(typeof(NTAccount));
            var groupName = builtinUsers.Value.Split('\\')[^1];
            var sidBytes = new byte[userSid.BinaryLength];
            userSid.GetBinaryForm(sidBytes, 0);
            var sidPointer = Marshal.AllocHGlobal(sidBytes.Length);
            try
            {
                Marshal.Copy(sidBytes, 0, sidPointer, sidBytes.Length);
                var member = new LocalGroupMembersInfo0 { Sid = sidPointer };
                var status = NetLocalGroupAddMembers(
                    null,
                    groupName,
                    0,
                    ref member,
                    1);
                if (status is not Success and not MemberAlreadyInAlias)
                {
                    throw new InvalidOperationException(
                        $"Could not add the staged Agent account to the built-in Users group (NetLocalGroupAddMembers {status}).");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sidPointer);
            }
        }

        private static void GrantServiceLogonRight(SecurityIdentifier userSid) =>
            ChangeAccountRight(userSid, add: true);

        private static void RemoveServiceLogonRight(SecurityIdentifier userSid) =>
            ChangeAccountRight(userSid, add: false);

        private static void RemoveServiceLogonRightIfPresent(SecurityIdentifier userSid)
        {
            var attributes = new LsaObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<LsaObjectAttributes>())
            };
            var status = LsaOpenPolicy(
                IntPtr.Zero,
                ref attributes,
                PolicyLookupNames,
                out var policy);
            if (status != 0)
            {
                policy.Dispose();
                ThrowIfLsaFailed(
                    status,
                    "Could not open Local Security Policy for run-scoped staged Agent cleanup.");
            }

            using (policy)
            {
                var sidBytes = new byte[userSid.BinaryLength];
                userSid.GetBinaryForm(sidBytes, 0);
                var sidPointer = Marshal.AllocHGlobal(sidBytes.Length);
                var rightBuffer = Marshal.StringToHGlobalUni(ServiceLogonRight);
                try
                {
                    Marshal.Copy(sidBytes, 0, sidPointer, sidBytes.Length);
                    if (!HasAccountRight(policy, sidPointer, ServiceLogonRight))
                    {
                        return;
                    }

                    var right = new LsaUnicodeString
                    {
                        Length = checked((ushort)(ServiceLogonRight.Length * sizeof(char))),
                        MaximumLength = checked((ushort)((ServiceLogonRight.Length + 1) * sizeof(char))),
                        Buffer = rightBuffer
                    };
                    status = LsaRemoveAccountRights(
                        policy,
                        sidPointer,
                        removeAllRights: false,
                        [right],
                        1);
                    ThrowIfLsaFailed(
                        status,
                        $"Could not remove {ServiceLogonRight} from run-scoped staged Agent account '{userSid.Value}'.");
                    if (HasAccountRight(policy, sidPointer, ServiceLogonRight))
                    {
                        throw new InvalidOperationException(
                            $"Run-scoped staged Agent account '{userSid.Value}' still owns {ServiceLogonRight} after cleanup.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(rightBuffer);
                    Marshal.FreeHGlobal(sidPointer);
                }
            }
        }

        private static void ChangeAccountRight(
            SecurityIdentifier userSid,
            bool add)
        {
            var attributes = new LsaObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<LsaObjectAttributes>())
            };
            var status = LsaOpenPolicy(
                IntPtr.Zero,
                ref attributes,
                PolicyLookupNames | (add ? PolicyCreateAccount : 0),
                out var policy);
            if (status != 0)
            {
                policy.Dispose();
                ThrowIfLsaFailed(
                    status,
                    "Could not open Local Security Policy for the staged Agent service account.");
            }
            using (policy)
            {
                var sidBytes = new byte[userSid.BinaryLength];
                userSid.GetBinaryForm(sidBytes, 0);
                var sidPointer = Marshal.AllocHGlobal(sidBytes.Length);
                var rightBuffer = Marshal.StringToHGlobalUni(ServiceLogonRight);
                try
                {
                    Marshal.Copy(sidBytes, 0, sidPointer, sidBytes.Length);
                    var right = new LsaUnicodeString
                    {
                        Length = checked((ushort)(ServiceLogonRight.Length * sizeof(char))),
                        MaximumLength = checked((ushort)((ServiceLogonRight.Length + 1) * sizeof(char))),
                        Buffer = rightBuffer
                    };
                    var rightPresentBefore = HasAccountRight(
                        policy,
                        sidPointer,
                        ServiceLogonRight);
                    if (rightPresentBefore != !add)
                    {
                        throw new InvalidOperationException(
                            add
                                ? $"Fresh staged Agent account '{userSid.Value}' unexpectedly already owns {ServiceLogonRight}."
                                : $"Staged Agent account '{userSid.Value}' no longer owns the {ServiceLogonRight} grant that this test must remove.");
                    }

                    status = add
                        ? LsaAddAccountRights(policy, sidPointer, [right], 1)
                        : LsaRemoveAccountRights(
                            policy,
                            sidPointer,
                            removeAllRights: false,
                            [right],
                            1);
                    ThrowIfLsaFailed(
                        status,
                        add
                            ? $"Could not grant {ServiceLogonRight} to staged Agent account '{userSid.Value}'."
                            : $"Could not remove {ServiceLogonRight} from staged Agent account '{userSid.Value}'.");
                    var rightPresentAfter = HasAccountRight(
                        policy,
                        sidPointer,
                        ServiceLogonRight);
                    if (rightPresentAfter != add)
                    {
                        throw new InvalidOperationException(
                            $"Local Security Policy did not persist the required {(add ? "grant" : "removal")} of {ServiceLogonRight} for '{userSid.Value}'.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(rightBuffer);
                    Marshal.FreeHGlobal(sidPointer);
                }
            }
        }

        private static bool HasAccountRight(
            SafeLsaPolicyHandle policy,
            IntPtr accountSid,
            string expectedRight)
        {
            var status = LsaEnumerateAccountRights(
                policy,
                accountSid,
                out var rights,
                out var count);
            if (status == StatusObjectNameNotFound)
            {
                return false;
            }

            ThrowIfLsaFailed(
                status,
                "Could not enumerate staged Agent service-account rights.");
            var present = false;
            uint freeStatus = 0;
            try
            {
                var stride = Marshal.SizeOf<LsaUnicodeString>();
                for (var index = 0u; index < count; index++)
                {
                    var right = Marshal.PtrToStructure<LsaUnicodeString>(
                        IntPtr.Add(rights, checked((int)index * stride)));
                    var name = right.Buffer == IntPtr.Zero
                        ? null
                        : Marshal.PtrToStringUni(
                            right.Buffer,
                            right.Length / sizeof(char));
                    if (string.Equals(name, expectedRight, StringComparison.Ordinal))
                    {
                        present = true;
                        break;
                    }
                }
            }
            finally
            {
                if (rights != IntPtr.Zero)
                {
                    freeStatus = LsaFreeMemory(rights);
                }
            }

            if (freeStatus != 0)
            {
                throw new Win32Exception(
                    checked((int)LsaNtStatusToWinError(freeStatus)),
                    "Could not release staged Agent LSA account-rights memory.");
            }

            return present;
        }

        private static void ThrowIfLsaFailed(uint status, string message)
        {
            if (status == 0)
            {
                return;
            }

            var win32Error = LsaNtStatusToWinError(status);
            throw new Win32Exception(
                checked((int)win32Error),
                $"{message} (NTSTATUS 0x{status:x8}, Win32 error {win32Error}).");
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserAdd(
            string? serverName,
            uint level,
            ref UserInfo1 buffer,
            out uint parameterError);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserDel(string? serverName, string userName);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserGetInfo(
            string? serverName,
            string userName,
            uint level,
            out IntPtr buffer);

        [DllImport("netapi32.dll")]
        private static extern uint NetApiBufferFree(IntPtr buffer);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetLocalGroupAddMembers(
            string? serverName,
            string groupName,
            uint level,
            ref LocalGroupMembersInfo0 buffer,
            uint totalEntries);

        [DllImport("advapi32.dll")]
        private static extern uint LsaOpenPolicy(
            IntPtr systemName,
            ref LsaObjectAttributes objectAttributes,
            uint desiredAccess,
            out SafeLsaPolicyHandle policyHandle);

        [DllImport("advapi32.dll")]
        private static extern uint LsaAddAccountRights(
            SafeLsaPolicyHandle policyHandle,
            IntPtr accountSid,
            [In] LsaUnicodeString[] userRights,
            uint countOfRights);

        [DllImport("advapi32.dll")]
        private static extern uint LsaRemoveAccountRights(
            SafeLsaPolicyHandle policyHandle,
            IntPtr accountSid,
            [MarshalAs(UnmanagedType.U1)] bool removeAllRights,
            [In] LsaUnicodeString[] userRights,
            uint countOfRights);

        [DllImport("advapi32.dll")]
        private static extern uint LsaEnumerateAccountRights(
            SafeLsaPolicyHandle policyHandle,
            IntPtr accountSid,
            out IntPtr userRights,
            out uint countOfRights);

        [DllImport("advapi32.dll")]
        private static extern uint LsaFreeMemory(IntPtr buffer);

        [DllImport("advapi32.dll")]
        private static extern uint LsaNtStatusToWinError(uint status);

        [DllImport(
            "userenv.dll",
            EntryPoint = "DeleteProfileW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteProfile(
            string sidString,
            string? profilePath,
            string? computerName);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFileAttributesW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        private static extern uint GetFileAttributes(string fileName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct UserInfo1
        {
            public string? Name;
            public string? Password;
            public uint PasswordAge;
            public uint Privilege;
            public string? HomeDirectory;
            public string? Comment;
            public uint Flags;
            public string? ScriptPath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LocalGroupMembersInfo0
        {
            public IntPtr Sid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LsaObjectAttributes
        {
            public uint Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LsaUnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        private sealed class SafeLsaPolicyHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeLsaPolicyHandle()
                : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle() => LsaClose(handle) == 0;

            [DllImport("advapi32.dll")]
            private static extern uint LsaClose(IntPtr policyHandle);
        }

        private sealed record GrantedDirectoryAccess(
            string Path,
            FileSystemAccessRule Rule);

        public sealed record RunScopedIdentity(
            string AccountName,
            string Sid,
            bool AccountExists);
    }

    [SupportedOSPlatform("windows")]
    private sealed class RabbitMqWindowsServiceOutage
    {
        private const string WindowsServicePrefix = "windows-service:";
        private const string PortableProcessPrefix = "portable-process:";
        private readonly string? _serviceName;
        private readonly string? _portableServerScript;
        private readonly string? _erlangHome;
        private readonly string? _rabbitMqBase;
        private readonly IReadOnlyDictionary<string, string>? _portableEnvironment;
        private readonly string _host;
        private readonly int _port;
        private Process? _portableProcess;
        private bool _stoppedByThisTest;

        private RabbitMqWindowsServiceOutage(
            string? serviceName,
            string? portableServerScript,
            string? erlangHome,
            string? rabbitMqBase,
            IReadOnlyDictionary<string, string>? portableEnvironment,
            string host,
            int port)
        {
            _serviceName = serviceName;
            _portableServerScript = portableServerScript;
            _erlangHome = erlangHome;
            _rabbitMqBase = rabbitMqBase;
            _portableEnvironment = portableEnvironment;
            _host = host;
            _port = port;
        }

        public string ControlMode => _serviceName is null
            ? "portable-process"
            : "windows-service";

        public static RabbitMqWindowsServiceOutage CreateRequired(Uri brokerUri)
        {
            var control = RequiredText(
                Environment.GetEnvironmentVariable(BrokerOutageControlVariable),
                BrokerOutageControlVariable);
            if (brokerUri.Host is not ("127.0.0.1" or "localhost" or "::1"))
            {
                throw new InvalidOperationException(
                    "The staged broker-outage gate may stop only a loopback RabbitMQ service.");
            }

            if (control.StartsWith(WindowsServicePrefix, StringComparison.Ordinal)
                && control.Length > WindowsServicePrefix.Length
                && !control[WindowsServicePrefix.Length..].Any(char.IsWhiteSpace))
            {
                return new RabbitMqWindowsServiceOutage(
                    control[WindowsServicePrefix.Length..],
                    null,
                    null,
                    null,
                    null,
                    brokerUri.Host,
                    brokerUri.Port);
            }

            if (control.StartsWith(PortableProcessPrefix, StringComparison.Ordinal))
            {
                var serverScript = RequiredCanonicalFile(
                    control[PortableProcessPrefix.Length..],
                    BrokerOutageControlVariable,
                    "rabbitmq-server.bat");
                var erlangHome = RequiredCanonicalDirectoryEnvironment("ERLANG_HOME");
                var rabbitMqBase = RequiredCanonicalDirectoryEnvironment("RABBITMQ_BASE");
                var portableEnvironment = ReadRequiredPortableEnvironment(
                    brokerUri.Port);
                _ = RequiredCanonicalFile(
                    Path.Combine(erlangHome, "bin", "erl.exe"),
                    "ERLANG_HOME runtime",
                    "erl.exe");
                return new RabbitMqWindowsServiceOutage(
                    null,
                    serverScript,
                    erlangHome,
                    rabbitMqBase,
                    portableEnvironment,
                    brokerUri.Host,
                    brokerUri.Port);
            }

            throw new InvalidDataException(
                $"{BrokerOutageControlVariable} must be 'windows-service:<canonical-name>' "
                + "or 'portable-process:<absolute rabbitmq-server.bat>'.");
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            if (_serviceName is not null)
            {
                await RunServiceControlAsync("stop", timeout);
            }
            else
            {
                await StopPortableProcessTreeAsync(timeout);
            }

            _stoppedByThisTest = true;
            await WaitForPortAsync(expectedOpen: false, timeout);
        }

        public async Task StartAsync(TimeSpan timeout)
        {
            if (!_stoppedByThisTest)
            {
                throw new InvalidOperationException(
                    "The staged E2E cannot start a broker it did not stop.");
            }

            if (_serviceName is not null)
            {
                await RunServiceControlAsync("start", timeout);
            }
            else
            {
                StartPortableProcess();
            }

            await WaitForPortAsync(expectedOpen: true, timeout);
            _stoppedByThisTest = false;
        }

        public async Task EnsureStartedAsync(TimeSpan timeout)
        {
            if (!_stoppedByThisTest)
            {
                return;
            }

            try
            {
                if (_serviceName is not null)
                {
                    await RunServiceControlAsync("start", timeout);
                }
                else
                {
                    StartPortableProcess();
                }
            }
            finally
            {
                await WaitForPortAsync(expectedOpen: true, timeout);
                _stoppedByThisTest = false;
            }
        }

        private async Task RunServiceControlAsync(string action, TimeSpan timeout)
        {
            if (_serviceName is null)
            {
                throw new InvalidOperationException(
                    "Windows service control was requested for a portable broker.");
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { action, _serviceName }
            }) ?? throw new InvalidOperationException(
                $"Could not start Windows service control for RabbitMQ {action}.");
            using var cancellation = new CancellationTokenSource(timeout);
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Windows service control '{action} {_serviceName}' failed with "
                    + $"exit code {process.ExitCode}: {standardOutput} {standardError}");
            }
        }

        private void StartPortableProcess()
        {
            if (_portableServerScript is null
                || _erlangHome is null
                || _rabbitMqBase is null
                || _portableEnvironment is null)
            {
                throw new InvalidOperationException(
                    "Portable RabbitMQ process control is not configured.");
            }

            _portableProcess?.Dispose();
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /s /c \"\"{_portableServerScript}\"\"",
                WorkingDirectory = Path.GetDirectoryName(_portableServerScript)!,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var requiredEnvironment = new Dictionary<string, string>(
                _portableEnvironment,
                StringComparer.Ordinal)
            {
                ["ERLANG_HOME"] = _erlangHome,
                ["RABBITMQ_BASE"] = _rabbitMqBase
            };
            foreach (var (name, value) in requiredEnvironment)
            {
                if (!string.Equals(
                        Environment.GetEnvironmentVariable(name),
                        value,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Portable RabbitMQ environment '{name}' changed after validation.");
                }
            }

            _portableProcess = Process.Start(startInfo)
                               ?? throw new InvalidOperationException(
                                    "Could not start the portable RabbitMQ broker process.");
        }

        private async Task StopPortableProcessTreeAsync(TimeSpan timeout)
        {
            if (_erlangHome is null)
            {
                throw new InvalidOperationException(
                    "Portable RabbitMQ process control has no validated ERLANG_HOME.");
            }

            var rootPid = _portableProcess is { HasExited: false }
                ? _portableProcess.Id
                : await FindListeningProcessIdAsync(timeout);
            if (_portableProcess is null)
            {
                using var listener = Process.GetProcessById(rootPid);
                var executable = listener.MainModule?.FileName
                                 ?? throw new InvalidOperationException(
                                     "Cannot validate the portable RabbitMQ listener executable.");
                if (!IsUnderDirectory(executable, _erlangHome))
                {
                    throw new InvalidOperationException(
                        $"RabbitMQ port owner '{executable}' is outside the authorized ERLANG_HOME.");
                }
            }

            await KillProcessTreeAsync(rootPid, timeout, allowAlreadyExited: false);

            _portableProcess?.Dispose();
            _portableProcess = null;
        }

        private async Task<int> FindListeningProcessIdAsync(TimeSpan timeout)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netstat.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "-ano", "-p", "tcp" }
            }) ?? throw new InvalidOperationException(
                "Could not inspect the portable RabbitMQ listener process.");
            using var cancellation = new CancellationTokenSource(timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"netstat failed with exit code {process.ExitCode}: {error}");
            }

            var suffix = $":{_port}";
            var candidates = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 5
                                && string.Equals(parts[0], "TCP", StringComparison.OrdinalIgnoreCase)
                                && parts[1].EndsWith(suffix, StringComparison.Ordinal)
                                && string.Equals(parts[3], "LISTENING", StringComparison.OrdinalIgnoreCase)
                                && int.TryParse(
                                    parts[4],
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    out _))
                .Select(parts => int.Parse(parts[4], CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray();
            return candidates.Length == 1
                ? candidates[0]
                : throw new InvalidOperationException(
                    $"Expected exactly one RabbitMQ listener PID on port {_port}; found {candidates.Length}.");
        }

        private static async Task KillProcessTreeAsync(
            int processId,
            TimeSpan timeout,
            bool allowAlreadyExited)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "/PID",
                    processId.ToString(CultureInfo.InvariantCulture),
                    "/T",
                    "/F"
                }
            }) ?? throw new InvalidOperationException(
                $"Could not start process-tree termination for PID {processId}.");
            using var cancellation = new CancellationTokenSource(timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0 && !allowAlreadyExited)
            {
                throw new InvalidOperationException(
                    $"taskkill failed for PID {processId} with exit code {process.ExitCode}: "
                    + $"{output} {error}");
            }
        }

        private static string RequiredCanonicalDirectoryEnvironment(string variableName)
        {
            var value = RequiredText(
                Environment.GetEnvironmentVariable(variableName),
                variableName);
            if (!Path.IsPathFullyQualified(value)
                || !string.Equals(
                    Path.GetFullPath(value),
                    value.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{variableName} must be a canonical absolute directory path.");
            }

            var path = Path.GetFullPath(value);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(
                    $"{variableName} does not exist: {path}");
            }

            EnsureNoReparsePoint(path, variableName);
            return path;
        }

        private static Dictionary<string, string>
            ReadRequiredPortableEnvironment(int brokerPort)
        {
            var environment = Environment.GetEnvironmentVariables()
                .Cast<DictionaryEntry>()
                .Where(static entry => entry.Key is string name
                                       && name.StartsWith(
                                           "RABBITMQ_",
                                           StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    static entry => (string)entry.Key,
                    static entry => RequiredText(
                        entry.Value as string,
                        (string)entry.Key),
                    StringComparer.OrdinalIgnoreCase);
            var nodeName = RequiredText(
                Environment.GetEnvironmentVariable("RABBITMQ_NODENAME"),
                "RABBITMQ_NODENAME");
            if (!nodeName.Contains('@')
                || nodeName.Any(char.IsWhiteSpace))
            {
                throw new InvalidDataException(
                    "RABBITMQ_NODENAME must be a canonical explicit node@host name.");
            }

            var nodePort = ReadRequiredPort("RABBITMQ_NODE_PORT");
            var distributionPort = ReadRequiredPort("RABBITMQ_DIST_PORT");
            if (nodePort != brokerPort)
            {
                throw new InvalidDataException(
                    $"RABBITMQ_NODE_PORT {nodePort} does not match broker port {brokerPort}.");
            }

            if (distributionPort == nodePort)
            {
                throw new InvalidDataException(
                    "RABBITMQ_DIST_PORT must differ from RABBITMQ_NODE_PORT.");
            }

            return environment;
        }

        private static int ReadRequiredPort(string variableName)
        {
            var value = RequiredText(
                Environment.GetEnvironmentVariable(variableName),
                variableName);
            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port)
                || port is <= 0 or > 65_535)
            {
                throw new InvalidDataException(
                    $"{variableName} must be a canonical TCP port number.");
            }

            return port;
        }

        private static string RequiredCanonicalFile(
            string value,
            string name,
            string expectedFileName)
        {
            var canonical = RequiredText(value, name);
            if (!Path.IsPathFullyQualified(canonical)
                || !string.Equals(
                    Path.GetFullPath(canonical),
                    canonical,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(canonical),
                    expectedFileName,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(canonical))
            {
                throw new InvalidDataException(
                    $"{name} must identify an existing canonical absolute {expectedFileName} file.");
            }

            EnsureNoReparsePoint(canonical, name);
            return canonical;
        }

        private static void EnsureNoReparsePoint(string path, string name)
        {
            var current = Path.GetFullPath(path);
            if (File.Exists(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"{name} cannot be a reparse point.");
                }

                current = Path.GetDirectoryName(current)!;
            }

            while (!string.IsNullOrEmpty(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        $"{name} cannot traverse a reparse point: {current}");
                }

                current = Path.GetDirectoryName(current)!;
            }
        }

        private static bool IsUnderDirectory(string path, string root)
        {
            var canonicalRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var prefix = canonicalRoot + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        private async Task WaitForPortAsync(bool expectedOpen, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                using var client = new System.Net.Sockets.TcpClient();
                var isOpen = false;
                try
                {
                    await client.ConnectAsync(_host, _port)
                        .WaitAsync(TimeSpan.FromSeconds(1));
                    isOpen = client.Connected;
                }
                catch (Exception exception) when (exception is SocketException
                                                  or TimeoutException)
                {
                    isOpen = false;
                }

                if (isOpen == expectedOpen)
                {
                    return;
                }

                if (expectedOpen && _portableProcess is { HasExited: true })
                {
                    throw new InvalidOperationException(
                        $"Portable RabbitMQ exited with code {_portableProcess.ExitCode} "
                        + $"before {_host}:{_port} opened. {ReadPortableDiagnostics()}");
                }

                await Task.Delay(250);
            }

            throw new TimeoutException(
                $"RabbitMQ port {_host}:{_port} did not become "
                + (expectedOpen ? "available. " : "unavailable. ")
                + ReadPortableDiagnostics());
        }

        private string ReadPortableDiagnostics()
        {
            return _rabbitMqBase is null
                ? string.Empty
                : $"Inspect RabbitMQ logs beneath '{Path.Combine(_rabbitMqBase, "log")}'.";
        }
    }

    private sealed class BrokerTopology : IAsyncDisposable
    {
        private readonly IConnection _connection;
        private readonly IChannel _channel;
        private readonly string _jobQueue;
        private readonly string _resultQueue;
        private readonly string[] _safetyQueues;

        private BrokerTopology(
            IConnection connection,
            IChannel channel,
            string jobQueue,
            string resultQueue,
            string[] safetyQueues)
        {
            _connection = connection;
            _channel = channel;
            _jobQueue = jobQueue;
            _resultQueue = resultQueue;
            _safetyQueues = safetyQueues;
        }

        public static async ValueTask<BrokerTopology> CreateAsync(
            Uri brokerUri,
            StationCoordinatorTransportOptions options,
            string agentId,
            string stationId)
        {
            var factory = new ConnectionFactory
            {
                Uri = brokerUri,
                ClientProvidedName = $"openlineops-staged-e2e-topology-{agentId}",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };
            var connection = await factory.CreateConnectionAsync();
            var channel = await connection.CreateChannelAsync();
            try
            {
                await channel.ExchangeDeclareAsync(
                    options.JobExchange,
                    ExchangeType.Direct,
                    durable: true,
                    autoDelete: false);
                await channel.ExchangeDeclareAsync(
                    options.EventExchange,
                    ExchangeType.Topic,
                    durable: true,
                    autoDelete: false);
                var jobQueue = StationTransportRoute.JobQueue(agentId, stationId);
                await channel.QueueDeclareAsync(jobQueue, true, false, false);
                await channel.QueueBindAsync(
                    jobQueue,
                    options.JobExchange,
                    StationTransportRoute.Job(agentId, stationId));
                await channel.QueueBindAsync(
                    jobQueue,
                    options.JobExchange,
                    StationTransportRoute.ResourceLeaseChanged(agentId, stationId));
                var resultQueue =
                    $"openlineops.coordinator.{options.CoordinatorId}.station-results";
                await channel.QueueDeclareAsync(resultQueue, true, false, false);
                foreach (var kind in new[]
                         {
                             nameof(StationJobAccepted),
                             nameof(StationJobProgressed),
                             nameof(StationJobCompleted),
                             nameof(StationJobRecoveryRequired),
                             nameof(MaterialArrived),
                             nameof(AgentPresenceReported)
                         })
                {
                    await channel.QueueBindAsync(
                        resultQueue,
                        options.EventExchange,
                        StationTransportRoute.EventPattern(kind));
                }

                var safetyQueues = new[]
                {
                    StationTransportRoute.SafetyQueue(
                        agentId,
                        stationId,
                        "emergency-stop"),
                    StationTransportRoute.SafetyQueue(
                        agentId,
                        stationId,
                        "safe-stop"),
                    StationTransportRoute.SafetyQueue(
                        agentId,
                        stationId,
                        "job-cancel")
                };
                return new BrokerTopology(
                    connection,
                    channel,
                    jobQueue,
                    resultQueue,
                    safetyQueues);
            }
            catch
            {
                await channel.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task WaitForAgentConsumerAsync(
            WindowsAgentProcess agent,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (agent.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Staged Agent exited before consuming jobs with code {agent.ExitCode}.");
                }

                var state = await _channel.QueueDeclarePassiveAsync(_jobQueue);
                if (state.ConsumerCount > 0)
                {
                    return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException(
                $"Staged Agent did not attach a consumer to '{_jobQueue}'.");
        }

        public async ValueTask DeleteQueuesAsync()
        {
            if (!_channel.IsOpen)
            {
                return;
            }

            foreach (var queue in _safetyQueues.Append(_jobQueue).Append(_resultQueue))
            {
                try
                {
                    await _channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
                }
                catch (RabbitMQ.Client.Exceptions.OperationInterruptedException exception)
                    when (exception.ShutdownReason?.ReplyCode == 404)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _channel.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsAgentService : IDisposable
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ScManagerCreateService = 0x0002;
        private const uint DeleteAccess = 0x00010000;
        private const uint ServiceQueryConfig = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceStop = 0x0020;
        private const uint ServiceWin32OwnProcess = 0x00000010;
        private const uint ServiceDemandStart = 0x00000003;
        private const uint ServiceErrorNormal = 0x00000001;
        private const uint ServiceControlStop = 0x00000001;
        private const uint ScStatusProcessInfo = 0;
        private const uint ServiceConfigFailureActions = 2;
        private const uint ServiceStopped = 0x00000001;
        private const uint ServiceStartPending = 0x00000002;
        private const uint ServiceStopPending = 0x00000003;
        private const uint ServiceRunning = 0x00000004;
        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorServiceNotActive = 1062;
        private const int ErrorServiceAlreadyRunning = 1056;
        private const int ErrorServiceMarkedForDelete = 1072;
        private const int ErrorInsufficientBuffer = 122;
        private const string ServiceRegistryPrefix =
            @"SYSTEM\CurrentControlSet\Services\";
        private const string EventLogSourceRegistryPrefix =
            @"SYSTEM\CurrentControlSet\Services\EventLog\Application\";
        private static readonly TimeSpan ServiceTransitionTimeout =
            TimeSpan.FromSeconds(45);

        private readonly object _gate = new();
        private readonly SafeServiceHandle _serviceControlManager;
        private SafeServiceHandle? _service;
        private readonly string[] _environmentEntries;
        private readonly string _expectedExecutablePath;
        private readonly string _expectedExecutableSha256;
        private readonly string _expectedBinaryPath;
        private readonly string _expectedServiceAccountName;
        private readonly RestrictedAgentIdentity _identity;
        private readonly List<uint> _startedProcessIds = [];
        private readonly List<uint> _cleanlyStoppedProcessIds = [];
        private SafeProcessHandle? _activeProcessHandle;
        private uint? _activeProcessId;
        private bool _disposed;

        private WindowsAgentService(
            SafeServiceHandle serviceControlManager,
            SafeServiceHandle service,
            string serviceName,
            string[] environmentEntries,
            string expectedExecutablePath,
            string expectedExecutableSha256,
            string expectedBinaryPath,
            string expectedServiceAccountName,
            RestrictedAgentIdentity identity)
        {
            _serviceControlManager = serviceControlManager;
            _service = service;
            ServiceName = serviceName;
            _environmentEntries = environmentEntries;
            _expectedExecutablePath = expectedExecutablePath;
            _expectedExecutableSha256 = expectedExecutableSha256;
            _expectedBinaryPath = expectedBinaryPath;
            _expectedServiceAccountName = expectedServiceAccountName;
            _identity = identity;
        }

        public string ServiceName { get; }

        public bool DeletionProven { get; private set; }

        public bool LifecycleVerified
        {
            get
            {
                lock (_gate)
                {
                    return !_disposed
                           && _startedProcessIds.Count == 2
                           && _cleanlyStoppedProcessIds.SequenceEqual(
                               _startedProcessIds)
                           && _startedProcessIds.Distinct().Count() == 2
                           && QueryStatus(RequiredService(), ServiceName).CurrentState
                           == ServiceStopped;
                }
            }
        }

        public static WindowsAgentService Install(
            string executablePath,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            RestrictedAgentIdentity identity,
            string suffix)
        {
            ArgumentNullException.ThrowIfNull(environment);
            ArgumentNullException.ThrowIfNull(identity);
            var fullExecutablePath = Path.GetFullPath(executablePath);
            var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
            if (!File.Exists(fullExecutablePath))
            {
                throw new FileNotFoundException(
                    "The staged Agent service executable is missing.",
                    fullExecutablePath);
            }

            if (!Directory.Exists(fullWorkingDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"The staged Agent service content root '{fullWorkingDirectory}' is missing.");
            }

            if (!string.Equals(
                    Path.GetDirectoryName(fullExecutablePath),
                    fullWorkingDirectory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The staged Agent service executable must be the direct child of its frozen content root.");
            }

            if (suffix.Length != 32
                || suffix.Any(static character =>
                    character is not (>= '0' and <= '9')
                        and not (>= 'a' and <= 'f')))
            {
                throw new ArgumentException(
                    "The staged Agent service suffix must contain exactly 32 lowercase hexadecimal characters.",
                    nameof(suffix));
            }

            var serviceName = $"OpenLineOpsAgentE2E-{suffix}";
            if (serviceName.Length > 80)
            {
                throw new InvalidOperationException(
                    "The staged Agent dynamic service name exceeds the ServiceBase limit.");
            }

            var serviceEnvironment = new Dictionary<string, string>(
                environment,
                StringComparer.OrdinalIgnoreCase)
            {
                ["OpenLineOps__WindowsServiceName"] = serviceName,
                ["DOTNET_CONTENTROOT"] = fullWorkingDirectory
            };
            var environmentEntries = CreateEnvironmentEntries(serviceEnvironment);
            var executableSha256 = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(fullExecutablePath)));
            var binaryPath = QuoteServiceBinaryPath(fullExecutablePath);
            var manager = OpenSCManager(
                machineName: null,
                databaseName: null,
                ScManagerConnect | ScManagerCreateService);
            if (manager.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                manager.Dispose();
                throw new Win32Exception(
                    error,
                    "Could not open the Windows Service Control Manager for staged Agent installation.");
            }

            SafeServiceHandle? service = null;
            var eventLogSourceRegistered = false;
            try
            {
                service = CreateService(
                    manager,
                    serviceName,
                    serviceName,
                    DeleteAccess
                    | ServiceQueryConfig
                    | ServiceQueryStatus
                    | ServiceStart
                    | ServiceStop,
                    ServiceWin32OwnProcess,
                    ServiceDemandStart,
                    ServiceErrorNormal,
                    binaryPath,
                    loadOrderGroup: null,
                    IntPtr.Zero,
                    dependencies: null,
                    identity.ServiceAccountName,
                    identity.Password);
                if (service.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    service.Dispose();
                    service = null;
                    throw new Win32Exception(
                        error,
                        $"Could not install staged Agent Windows service '{serviceName}'.");
                }

                identity.MarkServiceInstalled(serviceName);
                RegisterEventLogSource(
                    serviceName,
                    new SecurityIdentifier(identity.Sid),
                    out eventLogSourceRegistered);
                ProtectAndWriteEnvironment(serviceName, environmentEntries);
                VerifyEnvironment(serviceName, environmentEntries);
                VerifyConfiguration(
                    service,
                    serviceName,
                    binaryPath,
                    identity.ServiceAccountName);
                var owner = new WindowsAgentService(
                    manager,
                    service,
                    serviceName,
                    environmentEntries,
                    fullExecutablePath,
                    executableSha256,
                    binaryPath,
                    identity.ServiceAccountName,
                    identity);
                service = null;
                return owner;
            }
            catch (Exception exception)
            {
                var failures = new List<Exception> { exception };
                var eventLogSourceDeletionProven = !eventLogSourceRegistered;
                if (eventLogSourceRegistered)
                {
                    var failureCount = failures.Count;
                    CaptureCleanupFailure(
                        failures,
                        () => DeleteEventLogSource(
                            serviceName,
                            new SecurityIdentifier(identity.Sid)));
                    eventLogSourceDeletionProven = failures.Count == failureCount;
                }

                var serviceDeletionRequested = false;
                if (service is not null && !service.IsInvalid)
                {
                    if (eventLogSourceDeletionProven)
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => DeleteEnvironmentValue(serviceName));
                        var failureCount = failures.Count;
                        CaptureCleanupFailure(
                            failures,
                            () => DeleteServiceRequired(service, serviceName));
                        serviceDeletionRequested = failures.Count == failureCount;
                    }
                }

                CaptureCleanupFailure(failures, () => service?.Dispose());
                if (serviceDeletionRequested)
                {
                    CaptureCleanupFailure(
                        failures,
                        () =>
                        {
                            WaitForServiceDeletion(
                                manager,
                                serviceName,
                                TimeSpan.FromSeconds(15));
                            VerifyEventLogSourceAbsent(serviceName);
                            identity.MarkServiceDeletionProven(serviceName);
                        });
                }

                CaptureCleanupFailure(failures, manager.Dispose);
                if (failures.Count > 1)
                {
                    throw new AggregateException(
                        "Staged Agent service installation failed and rollback was incomplete.",
                        failures);
                }

                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }
        }

        public static void CleanupRunScoped(
            AgentServiceCleanupEntry entry,
            string expectedAccountSid)
        {
            _ = new SecurityIdentifier(expectedAccountSid);
            var manager = OpenSCManager(
                machineName: null,
                databaseName: null,
                ScManagerConnect);
            if (manager.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                manager.Dispose();
                throw new Win32Exception(
                    error,
                    "Could not open SCM for run-scoped staged Agent cleanup.");
            }

            using (manager)
            {
                var service = OpenService(
                    manager,
                    entry.ServiceName,
                    DeleteAccess | ServiceQueryConfig | ServiceQueryStatus | ServiceStop);
                if (service.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    service.Dispose();
                    if (error is ErrorServiceDoesNotExist or ErrorServiceMarkedForDelete)
                    {
                        WaitForServiceDeletion(
                            manager,
                            entry.ServiceName,
                            ServiceTransitionTimeout);
                        DeleteEventLogSource(
                            entry.ServiceName,
                            new SecurityIdentifier(expectedAccountSid));
                        VerifyEventLogSourceAbsent(entry.ServiceName);
                        return;
                    }

                    throw new Win32Exception(
                        error,
                        $"Could not open run-scoped staged Agent service '{entry.ServiceName}'.");
                }

                var failures = new List<Exception>();
                var serviceDeletionRequested = false;
                try
                {
                    VerifyConfiguration(
                        service,
                        entry.ServiceName,
                        QuoteServiceBinaryPath(entry.ExecutablePath),
                        $".\\{entry.AccountName}");
                    CaptureCleanupFailure(
                        failures,
                        () => DeleteEnvironmentValue(entry.ServiceName));
                    CaptureCleanupFailure(
                        failures,
                        () => DeleteEventLogSource(
                            entry.ServiceName,
                            new SecurityIdentifier(expectedAccountSid)));
                    var executableValidated = true;
                    try
                    {
                        VerifyCleanupExecutable(entry);
                    }
                    catch (Exception exception)
                    {
                        executableValidated = false;
                        failures.Add(exception);
                    }

                    if (executableValidated)
                    {
                        CaptureCleanupFailure(
                            failures,
                            () => StopRunScopedService(
                                service,
                                entry,
                                expectedAccountSid,
                                ServiceTransitionTimeout));
                        var failureCount = failures.Count;
                        CaptureCleanupFailure(
                            failures,
                            () => DeleteServiceRequired(service, entry.ServiceName));
                        serviceDeletionRequested = failures.Count == failureCount;
                    }
                }
                finally
                {
                    service.Dispose();
                }

                if (serviceDeletionRequested)
                {
                    CaptureCleanupFailure(
                        failures,
                        () => WaitForServiceDeletion(
                            manager,
                            entry.ServiceName,
                            ServiceTransitionTimeout));
                }

                CaptureCleanupFailure(
                    failures,
                    () => VerifyEventLogSourceAbsent(entry.ServiceName));
                if (failures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(failures[0]).Throw();
                }

                if (failures.Count > 1)
                {
                    throw new AggregateException(
                        $"Run-scoped service '{entry.ServiceName}' cleanup was incomplete.",
                        failures);
                }
            }
        }

        public static void ProveRunScopedArtifactsAbsent(
            AgentServiceCleanupEntry entry)
        {
            var manager = OpenSCManager(
                machineName: null,
                databaseName: null,
                ScManagerConnect);
            if (manager.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                manager.Dispose();
                throw new Win32Exception(
                    error,
                    "Could not open SCM to prove pre-account run-scope absence.");
            }

            using (manager)
            {
                var service = OpenService(
                    manager,
                    entry.ServiceName,
                    ServiceQueryStatus);
                if (!service.IsInvalid)
                {
                    service.Dispose();
                    throw new InvalidOperationException(
                        $"Service '{entry.ServiceName}' exists without a manifest-bound account SID; preserving it.");
                }

                var error = Marshal.GetLastWin32Error();
                service.Dispose();
                if (error != ErrorServiceDoesNotExist)
                {
                    throw new Win32Exception(
                        error,
                        $"Could not prove absence of pre-account service '{entry.ServiceName}'.");
                }

                using var serviceKey = Registry.LocalMachine.OpenSubKey(
                    ServiceRegistryPrefix + entry.ServiceName,
                    writable: false);
                if (serviceKey is not null)
                {
                    throw new InvalidOperationException(
                        $"Service registry key '{entry.ServiceName}' exists without a manifest-bound account SID; preserving it.");
                }
            }

            VerifyEventLogSourceAbsent(entry.ServiceName);
            if (File.Exists(entry.OwnedRoot))
            {
                throw new InvalidDataException(
                    $"Pre-account run-scoped root '{entry.OwnedRoot}' is a file; preserving it.");
            }

            if (Directory.Exists(entry.OwnedRoot))
            {
                VerifyRunScopedOwnedRootForCleanup(entry.OwnedRoot);
                if (File.Exists(entry.ExecutablePath))
                {
                    VerifyCleanupExecutable(entry);
                }
            }
        }

        private static void StopRunScopedService(
            SafeServiceHandle service,
            AgentServiceCleanupEntry entry,
            string expectedAccountSid,
            TimeSpan timeout)
        {
            var status = QueryStatus(service, entry.ServiceName);
            if (status.CurrentState == ServiceStopped)
            {
                return;
            }

            if (status.CurrentState == ServiceStartPending)
            {
                try
                {
                    status = WaitForState(
                        service,
                        ServiceRunning,
                        timeout,
                        entry.ServiceName);
                }
                catch (InvalidOperationException)
                    when (QueryStatus(service, entry.ServiceName).CurrentState == ServiceStopped)
                {
                    return;
                }
            }

            if (status.CurrentState == ServiceStopPending)
            {
                _ = WaitForState(
                    service,
                    ServiceStopped,
                    timeout,
                    entry.ServiceName);
                return;
            }

            if (status.CurrentState != ServiceRunning)
            {
                throw new InvalidOperationException(
                    $"Run-scoped staged Agent service '{entry.ServiceName}' is in unsupported cleanup state {DescribeState(status.CurrentState)}.");
            }

            SafeProcessHandle? validatedProcess = null;
            uint validatedProcessId = 0;
            try
            {
                if (status.ProcessId == 0)
                {
                    throw new InvalidOperationException(
                        $"Running service '{entry.ServiceName}' reported no PID.");
                }

                validatedProcess = WindowsAgentProcess.OpenRequiredProcess(status.ProcessId);
                var confirmed = QueryStatus(service, entry.ServiceName);
                if (confirmed.CurrentState == ServiceStopped)
                {
                    WindowsAgentProcess.WaitForExitRequired(
                        validatedProcess,
                        timeout,
                        status.ProcessId);
                    return;
                }

                if (confirmed.ProcessId != status.ProcessId
                    || confirmed.CurrentState != ServiceRunning)
                {
                    throw new InvalidOperationException(
                        $"Run-scoped service '{entry.ServiceName}' changed state or PID while cleanup secured its Running process handle.");
                }

                _ = WindowsAgentProcess.ValidateRequiredProcess(
                    validatedProcess,
                    status.ProcessId,
                    entry.ExecutablePath,
                    entry.ExecutableSha256,
                    expectedAccountSid);
                confirmed = QueryStatus(service, entry.ServiceName);
                if (confirmed.CurrentState == ServiceStopped)
                {
                    WindowsAgentProcess.WaitForExitRequired(
                        validatedProcess,
                        timeout,
                        status.ProcessId);
                    return;
                }

                if (confirmed.ProcessId != status.ProcessId
                    || confirmed.CurrentState != ServiceRunning)
                {
                    throw new InvalidOperationException(
                        $"Run-scoped service '{entry.ServiceName}' changed state or PID after exact token and image validation.");
                }

                validatedProcessId = status.ProcessId;
                var stopRequested = ControlService(service, ServiceControlStop, out _);
                var stopError = stopRequested ? 0 : Marshal.GetLastWin32Error();
                try
                {
                    if (!stopRequested && stopError != ErrorServiceNotActive)
                    {
                        throw new Win32Exception(
                            stopError,
                            $"Could not stop run-scoped staged Agent service '{entry.ServiceName}'.");
                    }

                    _ = WaitForState(service, ServiceStopped, timeout, entry.ServiceName);
                    WindowsAgentProcess.WaitForExitRequired(
                        validatedProcess,
                        timeout,
                        validatedProcessId);
                }
                catch (Exception exception)
                    when (exception is TimeoutException or Win32Exception)
                {
                    var current = QueryStatus(service, entry.ServiceName);
                    if (current.CurrentState == ServiceStopped)
                    {
                        WindowsAgentProcess.WaitForExitRequired(
                            validatedProcess,
                            timeout,
                            validatedProcessId);
                        return;
                    }

                    if (current.CurrentState == ServiceRunning
                        && current.ProcessId != validatedProcessId)
                    {
                        throw new InvalidOperationException(
                            $"Run-scoped service '{entry.ServiceName}' changed Running PID before forced cleanup.",
                            exception);
                    }

                    _ = WindowsAgentProcess.ValidateRequiredProcess(
                        validatedProcess,
                        validatedProcessId,
                        entry.ExecutablePath,
                        entry.ExecutableSha256,
                        expectedAccountSid!);
                    current = QueryStatus(service, entry.ServiceName);
                    if (current.CurrentState == ServiceStopped
                        || current.CurrentState == ServiceRunning
                        && current.ProcessId != validatedProcessId
                        || current.CurrentState is not (ServiceRunning or ServiceStopPending))
                    {
                        throw new InvalidOperationException(
                            $"Run-scoped service '{entry.ServiceName}' changed state or Running PID immediately before forced termination.",
                            exception);
                    }

                    if (!WindowsAgentProcess.IsExited(validatedProcess)
                        && !TerminateProcess(validatedProcess, exitCode: 1)
                        && !WindowsAgentProcess.IsExited(validatedProcess))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            $"Could not terminate validated run-scoped staged Agent PID {validatedProcessId}.");
                    }

                    WindowsAgentProcess.WaitForExitRequired(
                        validatedProcess,
                        timeout,
                        validatedProcessId);
                    _ = WaitForState(
                        service,
                        ServiceStopped,
                        timeout,
                        entry.ServiceName);
                }
            }
            finally
            {
                validatedProcess?.Dispose();
            }
        }

        public WindowsAgentProcess Start()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var service = RequiredService();
                var before = QueryStatus(service, ServiceName);
                if (before.CurrentState != ServiceStopped)
                {
                    throw new InvalidOperationException(
                        $"Staged Agent service '{ServiceName}' must be Stopped before Start; actual state is {DescribeState(before.CurrentState)}.");
                }

                VerifyEnvironment(ServiceName, _environmentEntries);
                VerifyConfiguration(
                    service,
                    ServiceName,
                    _expectedBinaryPath,
                    _expectedServiceAccountName);
                if (!StartService(service, argumentCount: 0, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        error,
                        error == ErrorServiceAlreadyRunning
                            ? $"Staged Agent service '{ServiceName}' was already running."
                            : $"Could not start staged Agent service '{ServiceName}'.");
                }

                SafeProcessHandle? processHandle = null;
                uint? securedProcessId = null;
                try
                {
                    var running = WaitForState(
                        service,
                        ServiceRunning,
                        ServiceTransitionTimeout,
                        ServiceName);
                    if (running.ProcessId == 0)
                    {
                        throw new InvalidOperationException(
                            $"Staged Agent service '{ServiceName}' reported Running without a process ID.");
                    }

                    securedProcessId = running.ProcessId;
                    processHandle = WindowsAgentProcess.OpenRequiredProcess(
                        running.ProcessId);
                    var confirmed = QueryStatus(service, ServiceName);
                    if (confirmed.CurrentState != ServiceRunning
                        || confirmed.ProcessId != running.ProcessId)
                    {
                        throw new InvalidOperationException(
                            $"Staged Agent service '{ServiceName}' changed state or PID while its process handle was secured.");
                    }

                    var process = WindowsAgentProcess.CreateValidated(
                        this,
                        processHandle,
                        running.ProcessId,
                        _expectedExecutablePath,
                        _expectedExecutableSha256,
                        _identity);
                    confirmed = QueryStatus(service, ServiceName);
                    if (confirmed.CurrentState != ServiceRunning
                        || confirmed.ProcessId != running.ProcessId)
                    {
                        throw new InvalidOperationException(
                            $"Staged Agent service '{ServiceName}' changed state or PID after exact token and frozen-image validation.");
                    }

                    _activeProcessId = running.ProcessId;
                    _activeProcessHandle = processHandle;
                    processHandle = null;
                    _startedProcessIds.Add(running.ProcessId);
                    return process;
                }
                catch (Exception exception)
                {
                    var failures = new List<Exception> { exception };
                    CaptureCleanupFailure(
                        failures,
                        () => StopOrTerminateCurrentProcess(ServiceTransitionTimeout));
                    CaptureCleanupFailure(
                        failures,
                        () =>
                        {
                            if (processHandle is not null
                                && securedProcessId is uint processId)
                            {
                                WindowsAgentProcess.WaitForExitRequired(
                                    processHandle,
                                    ServiceTransitionTimeout,
                                    processId);
                            }
                        });
                    CaptureCleanupFailure(
                        failures,
                        () => processHandle?.Dispose());
                    if (failures.Count > 1)
                    {
                        throw new AggregateException(
                            $"Staged Agent service '{ServiceName}' failed security validation and cleanup was incomplete.",
                            failures);
                    }

                    ExceptionDispatchInfo.Capture(exception).Throw();
                    throw;
                }
            }
        }

        internal int StopCleanly(
            uint expectedProcessId,
            SafeProcessHandle processHandle,
            TimeSpan timeout)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureExpectedActiveProcess(expectedProcessId);
                var service = RequiredService();
                var status = QueryStatus(service, ServiceName);
                if (status.CurrentState == ServiceStopped)
                {
                    WindowsAgentProcess.WaitForExitRequired(
                        processHandle,
                        timeout,
                        expectedProcessId);
                    var exitCode = WindowsAgentProcess.ReadExitCode(processHandle);
                    ReleaseActiveProcessHandle(expectedProcessId);
                    _cleanlyStoppedProcessIds.Add(expectedProcessId);
                    return exitCode;
                }

                if (status.CurrentState == ServiceRunning
                    && status.ProcessId != expectedProcessId)
                {
                    throw new InvalidOperationException(
                        $"Staged Agent service '{ServiceName}' changed from expected PID {expectedProcessId} to PID {status.ProcessId}.");
                }

                if (status.CurrentState != ServiceStopPending)
                {
                    if (!ControlService(
                            service,
                            ServiceControlStop,
                            out _))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceNotActive)
                        {
                            throw new Win32Exception(
                                error,
                                $"Could not stop staged Agent service '{ServiceName}'.");
                        }
                    }
                }

                var stopped = WaitForState(
                    service,
                    ServiceStopped,
                    timeout,
                    ServiceName);
                WindowsAgentProcess.WaitForExitRequired(
                    processHandle,
                    timeout,
                    expectedProcessId);
                var processExitCode = WindowsAgentProcess.ReadExitCode(processHandle);
                if (stopped.Win32ExitCode != 0
                    && stopped.Win32ExitCode != unchecked((uint)processExitCode))
                {
                    throw new InvalidOperationException(
                        $"Staged Agent service '{ServiceName}' stopped with SCM Win32 exit code {stopped.Win32ExitCode}, but process exit code was {processExitCode}.");
                }

                ReleaseActiveProcessHandle(expectedProcessId);
                _cleanlyStoppedProcessIds.Add(expectedProcessId);
                return processExitCode;
            }
        }

        internal int Kill(
            uint expectedProcessId,
            SafeProcessHandle processHandle)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_activeProcessId is null
                    && WindowsAgentProcess.IsExited(processHandle))
                {
                    return WindowsAgentProcess.ReadExitCode(processHandle);
                }

                EnsureExpectedActiveProcess(expectedProcessId);
                if (!WindowsAgentProcess.IsExited(processHandle))
                {
                    if (!TerminateProcess(processHandle, exitCode: 1)
                        && !WindowsAgentProcess.IsExited(processHandle))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            $"Could not terminate staged Agent service PID {expectedProcessId}.");
                    }
                }

                WindowsAgentProcess.WaitForExitRequired(
                    processHandle,
                    TimeSpan.FromSeconds(15),
                    expectedProcessId);
                var exitCode = WindowsAgentProcess.ReadExitCode(processHandle);
                _ = WaitForState(
                    RequiredService(),
                    ServiceStopped,
                    TimeSpan.FromSeconds(15),
                    ServiceName);
                ReleaseActiveProcessHandle(expectedProcessId);
                return exitCode;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var failures = new List<Exception>();
                CaptureCleanupFailure(
                    failures,
                    () => StopOrTerminateCurrentProcess(ServiceTransitionTimeout));
                CaptureCleanupFailure(
                    failures,
                    ReleaseAnyActiveProcessHandle);
                CaptureCleanupFailure(
                    failures,
                    () => DeleteEnvironmentValue(ServiceName));
                CaptureCleanupFailure(
                    failures,
                    () => DeleteEventLogSource(
                        ServiceName,
                        new SecurityIdentifier(_identity.Sid)));
                var service = _service;
                if (service is not null)
                {
                    CaptureCleanupFailure(
                        failures,
                        () => DeleteServiceRequired(service, ServiceName));
                    CaptureCleanupFailure(failures, service.Dispose);
                    _service = null;
                }

                var deletionProven = false;
                CaptureCleanupFailure(
                    failures,
                    () =>
                    {
                        WaitForServiceDeletion(
                            _serviceControlManager,
                            ServiceName,
                            TimeSpan.FromSeconds(15));
                        VerifyEventLogSourceAbsent(ServiceName);
                        deletionProven = true;
                    });
                DeletionProven = deletionProven;
                if (deletionProven)
                {
                    _identity.MarkServiceDeletionProven(ServiceName);
                }
                CaptureCleanupFailure(failures, _serviceControlManager.Dispose);
                _disposed = true;
                if (failures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(failures[0]).Throw();
                }

                if (failures.Count > 1)
                {
                    throw new AggregateException(
                        $"One or more cleanup steps failed for staged Agent service '{ServiceName}'.",
                        failures);
                }
            }
        }

        private void StopOrTerminateCurrentProcess(TimeSpan timeout)
        {
            var service = RequiredService();
            var status = QueryStatus(service, ServiceName);
            if (status.CurrentState == ServiceStopped)
            {
                if (_activeProcessHandle is not null
                    && _activeProcessId is uint processId)
                {
                    WindowsAgentProcess.WaitForExitRequired(
                        _activeProcessHandle,
                        timeout,
                        processId);
                }

                ReleaseAnyActiveProcessHandle();
                return;
            }

            if (status.CurrentState != ServiceStopPending
                && !ControlService(service, ServiceControlStop, out _))
            {
                var stopError = Marshal.GetLastWin32Error();
                if (stopError != ErrorServiceNotActive)
                {
                    TerminateServiceProcess(status, timeout);
                    return;
                }
            }

            try
            {
                _ = WaitForState(service, ServiceStopped, timeout, ServiceName);
                if (_activeProcessHandle is not null
                    && _activeProcessId is uint processId)
                {
                    WindowsAgentProcess.WaitForExitRequired(
                        _activeProcessHandle,
                        timeout,
                        processId);
                }

                ReleaseAnyActiveProcessHandle();
            }
            catch (TimeoutException)
            {
                status = QueryStatus(service, ServiceName);
                TerminateServiceProcess(status, timeout);
            }
        }

        private void TerminateServiceProcess(
            ServiceStatusProcess status,
            TimeSpan timeout)
        {
            var processId = _activeProcessId;
            var processHandle = _activeProcessHandle;
            if (processHandle is null)
            {
                if (status.CurrentState != ServiceRunning || status.ProcessId == 0)
                {
                    throw new InvalidOperationException(
                        $"Staged Agent service '{ServiceName}' could not stop and no retained Running process handle is available for forced cleanup.");
                }

                processHandle = WindowsAgentProcess.OpenRequiredProcess(status.ProcessId);
                var confirmed = QueryStatus(RequiredService(), ServiceName);
                if (confirmed.CurrentState != ServiceRunning
                    || confirmed.ProcessId != status.ProcessId)
                {
                    processHandle.Dispose();
                    throw new InvalidOperationException(
                        $"Staged Agent service '{ServiceName}' changed state or PID while cleanup secured its process handle.");
                }

                try
                {
                    _ = WindowsAgentProcess.CreateValidated(
                        this,
                        processHandle,
                        status.ProcessId,
                        _expectedExecutablePath,
                        _expectedExecutableSha256,
                        _identity);
                    confirmed = QueryStatus(RequiredService(), ServiceName);
                    if (confirmed.CurrentState != ServiceRunning
                        || confirmed.ProcessId != status.ProcessId)
                    {
                        throw new InvalidOperationException(
                            $"Staged Agent service '{ServiceName}' changed state or PID after cleanup validated its exact token and frozen image.");
                    }
                }
                catch
                {
                    processHandle.Dispose();
                    throw;
                }

                processId = status.ProcessId;
                _activeProcessId = processId;
                _activeProcessHandle = processHandle;
            }

            var requiredProcessId = processId
                ?? throw new InvalidOperationException(
                    "The staged Agent cleanup process ID was not captured.");
            if (!WindowsAgentProcess.IsExited(processHandle)
                && !TerminateProcess(processHandle, exitCode: 1)
                && !WindowsAgentProcess.IsExited(processHandle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not force-terminate staged Agent service PID {requiredProcessId}.");
            }

            WindowsAgentProcess.WaitForExitRequired(
                processHandle,
                timeout,
                requiredProcessId);
            _ = WaitForState(
                RequiredService(),
                ServiceStopped,
                timeout,
                ServiceName);
            ReleaseActiveProcessHandle(requiredProcessId);
        }

        private void ReleaseActiveProcessHandle(uint expectedProcessId)
        {
            EnsureExpectedActiveProcess(expectedProcessId);
            ReleaseAnyActiveProcessHandle();
        }

        private void ReleaseAnyActiveProcessHandle()
        {
            _activeProcessHandle?.Dispose();
            _activeProcessHandle = null;
            _activeProcessId = null;
        }

        private void EnsureExpectedActiveProcess(uint expectedProcessId)
        {
            if (_activeProcessId != expectedProcessId)
            {
                throw new InvalidOperationException(
                    $"Staged Agent PID {expectedProcessId} is not the active process owned by service '{ServiceName}'.");
            }
        }

        private SafeServiceHandle RequiredService() =>
            _service is { IsClosed: false, IsInvalid: false } service
                ? service
                : throw new ObjectDisposedException(nameof(WindowsAgentService));

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private static ServiceStatusProcess WaitForState(
            SafeServiceHandle service,
            uint requiredState,
            TimeSpan timeout,
            string serviceName)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    "The staged Agent service transition timeout must be positive.");
            }

            var elapsed = Stopwatch.StartNew();
            uint? observedState = null;
            uint? observedCheckpoint = null;
            var progressDeadline = TimeSpan.Zero;
            ServiceStatusProcess status;
            while (true)
            {
                status = QueryStatus(service, serviceName);
                if (status.CurrentState == requiredState)
                {
                    return status;
                }

                if (requiredState == ServiceRunning
                    && status.CurrentState == ServiceStopped)
                {
                    throw new InvalidOperationException(
                        $"Staged Agent service '{serviceName}' stopped during startup "
                        + $"(Win32 exit {status.Win32ExitCode}, service exit {status.ServiceSpecificExitCode}).");
                }

                var now = elapsed.Elapsed;
                if (now >= timeout)
                {
                    throw new TimeoutException(
                        $"Staged Agent service '{serviceName}' did not reach {DescribeState(requiredState)} "
                        + $"within {timeout}; last state was {DescribeState(status.CurrentState)} "
                        + $"at checkpoint {status.CheckPoint} with wait hint {status.WaitHint} ms.");
                }

                if (observedState != status.CurrentState)
                {
                    observedState = status.CurrentState;
                    observedCheckpoint = status.CheckPoint;
                    progressDeadline = now + ProgressWindow(status.WaitHint);
                }
                else if (observedCheckpoint is null
                         || status.CheckPoint > observedCheckpoint.Value)
                {
                    observedCheckpoint = status.CheckPoint;
                    progressDeadline = now + ProgressWindow(status.WaitHint);
                }
                else if (status.CheckPoint < observedCheckpoint.Value)
                {
                    throw new InvalidOperationException(
                        $"Staged Agent service '{serviceName}' SCM checkpoint regressed from "
                        + $"{observedCheckpoint.Value} to {status.CheckPoint} while state remained "
                        + $"{DescribeState(status.CurrentState)}.");
                }
                else if (now >= progressDeadline)
                {
                    throw new TimeoutException(
                        $"Staged Agent service '{serviceName}' made no SCM checkpoint progress "
                        + $"while waiting for {DescribeState(requiredState)}; state "
                        + $"{DescribeState(status.CurrentState)}, checkpoint {status.CheckPoint}, "
                        + $"wait hint {status.WaitHint} ms.");
                }

                var delay = PollInterval(status.WaitHint);
                var hardRemaining = timeout - now;
                var progressRemaining = progressDeadline - now;
                if (delay > hardRemaining)
                {
                    delay = hardRemaining;
                }

                if (delay > progressRemaining)
                {
                    delay = progressRemaining;
                }

                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay);
                }
            }
        }

        private static TimeSpan PollInterval(uint waitHintMilliseconds) =>
            TimeSpan.FromMilliseconds(Math.Clamp(
                waitHintMilliseconds / 10d,
                1_000d,
                10_000d));

        private static TimeSpan ProgressWindow(uint waitHintMilliseconds) =>
            TimeSpan.FromMilliseconds(waitHintMilliseconds == 0
                ? 10_000d
                : Math.Clamp(
                    (double)waitHintMilliseconds,
                    1_000d,
                    120_000d));

        private static ServiceStatusProcess QueryStatus(
            SafeServiceHandle service,
            string serviceName)
        {
            var status = new ServiceStatusProcess();
            if (!QueryServiceStatusEx(
                    service,
                    ScStatusProcessInfo,
                    ref status,
                    checked((uint)Marshal.SizeOf<ServiceStatusProcess>()),
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not query staged Agent service '{serviceName}'.");
            }

            return status;
        }

        private static void VerifyConfiguration(
            SafeServiceHandle service,
            string serviceName,
            string expectedBinaryPath,
            string expectedServiceAccountName)
        {
            _ = QueryServiceConfig(
                service,
                IntPtr.Zero,
                bufferSize: 0,
                out var requiredSize);
            var sizingError = Marshal.GetLastWin32Error();
            if (requiredSize == 0 || sizingError != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(
                    sizingError,
                    $"Could not size configuration for staged Agent service '{serviceName}'.");
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
            try
            {
                if (!QueryServiceConfig(
                        service,
                        buffer,
                        requiredSize,
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not read configuration for staged Agent service '{serviceName}'.");
                }

                var configuration = Marshal.PtrToStructure<QueryServiceConfiguration>(
                    buffer);
                var binaryPath = Marshal.PtrToStringUni(
                    configuration.BinaryPathName);
                var loadOrderGroup = Marshal.PtrToStringUni(
                    configuration.LoadOrderGroup);
                var dependencies = Marshal.PtrToStringUni(
                    configuration.Dependencies);
                var serviceAccountName = Marshal.PtrToStringUni(
                    configuration.ServiceStartName);
                var displayName = Marshal.PtrToStringUni(
                    configuration.DisplayName);
                if (configuration.ServiceType != ServiceWin32OwnProcess
                    || configuration.StartType != ServiceDemandStart
                    || configuration.ErrorControl != ServiceErrorNormal
                    || configuration.TagId != 0
                    || !string.IsNullOrEmpty(loadOrderGroup)
                    || !string.IsNullOrEmpty(dependencies)
                    || !string.Equals(
                        binaryPath,
                        expectedBinaryPath,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        serviceAccountName,
                        expectedServiceAccountName,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        displayName,
                        serviceName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Staged Agent service '{serviceName}' SCM configuration differs from the exact own-process, demand-start, error-normal, frozen-image, dedicated-account, display-name, empty-group, empty-dependencies contract.");
                }

                VerifyFailureActions(service, serviceName);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void VerifyFailureActions(
            SafeServiceHandle service,
            string serviceName)
        {
            _ = QueryServiceConfig2(
                service,
                ServiceConfigFailureActions,
                IntPtr.Zero,
                bufferSize: 0,
                out var requiredSize);
            var sizingError = Marshal.GetLastWin32Error();
            if (requiredSize == 0 || sizingError != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(
                    sizingError,
                    $"Could not size failure actions for staged Agent service '{serviceName}'.");
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
            try
            {
                if (!QueryServiceConfig2(
                        service,
                        ServiceConfigFailureActions,
                        buffer,
                        requiredSize,
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not read failure actions for staged Agent service '{serviceName}'.");
                }

                var failureActions = Marshal.PtrToStructure<ServiceFailureActions>(
                    buffer);
                if (failureActions.ResetPeriod != 0
                    || failureActions.ActionsCount != 0
                    || failureActions.Actions != IntPtr.Zero
                    || !string.IsNullOrEmpty(Marshal.PtrToStringUni(
                        failureActions.RebootMessage))
                    || !string.IsNullOrEmpty(Marshal.PtrToStringUni(
                        failureActions.Command)))
                {
                    throw new InvalidOperationException(
                        $"Staged Agent service '{serviceName}' must have no SCM failure action or automatic restart command.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string DescribeState(uint state) => state switch
        {
            ServiceStopped => "Stopped",
            ServiceStartPending => "StartPending",
            ServiceStopPending => "StopPending",
            ServiceRunning => "Running",
            _ => $"SCM state {state}"
        };

        private static string[] CreateEnvironmentEntries(
            Dictionary<string, string> environment)
        {
            if (environment.Count == 0)
            {
                throw new InvalidDataException(
                    "The staged Agent service environment cannot be empty.");
            }

            return environment
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair =>
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)
                        || pair.Key.Contains('=')
                        || pair.Key.Contains('\0')
                        || pair.Value.Contains('\0'))
                    {
                        throw new InvalidDataException(
                            $"Environment entry '{pair.Key}' cannot be represented by SCM.");
                    }

                    return $"{pair.Key}={pair.Value}";
                })
                .ToArray();
        }

        private static void ProtectAndWriteEnvironment(
            string serviceName,
            string[] environmentEntries)
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ServiceRegistryPrefix + serviceName,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.FullControl)
                ?? throw new InvalidOperationException(
                    $"SCM did not create registry state for staged Agent service '{serviceName}'.");
            var currentSid = WindowsIdentity.GetCurrent().User
                             ?? throw new InvalidOperationException(
                                 "The staged E2E process identity has no SID for service-key ACL protection.");
            var allowedSids = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                currentSid
            }.Distinct().ToArray();
            var security = new RegistrySecurity();
            security.SetOwner(currentSid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in allowedSids)
            {
                security.AddAccessRule(new RegistryAccessRule(
                    sid,
                    RegistryRights.FullControl,
                    InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            key.SetAccessControl(security);
            key.SetValue(
                "Environment",
                environmentEntries,
                RegistryValueKind.MultiString);
            key.Flush();
            VerifyServiceKeyAcl(key, allowedSids, currentSid);
        }

        private static void VerifyServiceKeyAcl(
            RegistryKey key,
            IReadOnlyCollection<SecurityIdentifier> allowedSids,
            SecurityIdentifier expectedOwner)
        {
            var security = key.GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            if (!security.AreAccessRulesProtected
                || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !string.Equals(owner.Value, expectedOwner.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The staged Agent service registry key still inherits ambient access rules or has an unexpected owner.");
            }

            var allowed = allowedSids
                .Select(static sid => sid.Value)
                .ToHashSet(StringComparer.Ordinal);
            var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier));
            foreach (RegistryAccessRule rule in rules)
            {
                if (rule.IsInherited
                    || rule.AccessControlType != AccessControlType.Allow
                    || rule.IdentityReference is not SecurityIdentifier sid
                    || !allowed.Contains(sid.Value))
                {
                    throw new InvalidOperationException(
                        $"The staged Agent service registry ACL contains unexpected rule '{rule.IdentityReference} {rule.AccessControlType} {rule.RegistryRights}'.");
                }
            }

            foreach (var sid in allowed)
            {
                if (!rules.Cast<RegistryAccessRule>().Any(rule =>
                        rule.AccessControlType == AccessControlType.Allow
                        && rule.IdentityReference is SecurityIdentifier identity
                        && string.Equals(identity.Value, sid, StringComparison.Ordinal)
                        && (rule.RegistryRights & RegistryRights.FullControl)
                        == RegistryRights.FullControl))
                {
                    throw new InvalidOperationException(
                        $"The staged Agent service registry ACL is missing FullControl for SID '{sid}'.");
                }
            }
        }

        private static void VerifyEnvironment(
            string serviceName,
            string[] expectedEntries)
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ServiceRegistryPrefix + serviceName,
                writable: false)
                ?? throw new InvalidOperationException(
                    $"Staged Agent service registry key '{serviceName}' is missing.");
            var actual = key.GetValue(
                "Environment",
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string[];
            if (actual is null
                || !actual.SequenceEqual(expectedEntries, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Staged Agent service '{serviceName}' environment did not round-trip exactly through SCM registry state.");
            }
        }

        private static void RegisterEventLogSource(
            string serviceName,
            SecurityIdentifier serviceAccountSid,
            out bool sourceCreated)
        {
            sourceCreated = false;
            var registryPath = EventLogSourceRegistryPrefix + serviceName;
            if (EventLog.SourceExists(serviceName))
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' already exists before staged Agent registration.");
            }

            try
            {
                EventLog.CreateEventSource(new EventSourceCreationData(
                    serviceName,
                    "Application"));
                sourceCreated = true;
                using var key = Registry.LocalMachine.OpenSubKey(
                    registryPath,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.FullControl)
                    ?? throw new InvalidOperationException(
                        $"Could not create EventLog source '{serviceName}'.");
                var currentSid = WindowsIdentity.GetCurrent().User
                                 ?? throw new InvalidOperationException(
                                     "The staged E2E process identity has no SID for EventLog source ACL protection.");
                var security = new RegistrySecurity();
                security.SetOwner(currentSid);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in new[]
                         {
                             new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                             new SecurityIdentifier(
                                 WellKnownSidType.BuiltinAdministratorsSid,
                                 null),
                             currentSid
                         }.Distinct())
                {
                    security.AddAccessRule(new RegistryAccessRule(
                        sid,
                        RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                }

                security.AddAccessRule(new RegistryAccessRule(
                    serviceAccountSid,
                    RegistryRights.ReadKey,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                key.SetAccessControl(security);
                key.Flush();
                VerifyEventLogSource(
                    serviceName,
                    serviceAccountSid,
                    currentSid);
            }
            catch (Exception exception)
            {
                if (!sourceCreated)
                {
                    throw;
                }

                var failures = new List<Exception> { exception };
                CaptureCleanupFailure(
                    failures,
                    () => EventLog.DeleteEventSource(serviceName));
                CaptureCleanupFailure(
                    failures,
                    () => VerifyEventLogSourceAbsent(serviceName));
                if (failures.Count > 1)
                {
                    throw new AggregateException(
                        $"EventLog source '{serviceName}' registration failed and rollback was incomplete.",
                        failures);
                }

                sourceCreated = false;
                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }
        }

        private static void VerifyEventLogSource(
            string serviceName,
            SecurityIdentifier serviceAccountSid,
            SecurityIdentifier currentSid)
        {
            if (!EventLog.SourceExists(serviceName))
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' is missing after registration.");
            }


            if (!string.Equals(
                    EventLog.LogNameFromSourceName(serviceName, "."),
                    "Application",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' is not registered to the Application log.");
            }

            using var key = Registry.LocalMachine.OpenSubKey(
                EventLogSourceRegistryPrefix + serviceName,
                writable: false)
                ?? throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' is missing after registration.");
            var security = key.GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            if (!security.AreAccessRulesProtected
                || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !string.Equals(owner.Value, currentSid.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' still inherits ambient registry access or has an unexpected owner.");
            }

            var fullControlSids = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null).Value,
                currentSid.Value
            }.ToHashSet(StringComparer.Ordinal);
            var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier)).Cast<RegistryAccessRule>().ToArray();
            if (rules.Any(rule => rule.IsInherited
                                  || rule.AccessControlType != AccessControlType.Allow
                                  || rule.IdentityReference is not SecurityIdentifier sid
                                  || !fullControlSids.Contains(sid.Value)
                                  && !string.Equals(
                                      sid.Value,
                                      serviceAccountSid.Value,
                                      StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' ACL contains an unexpected rule.");
            }

            if (!rules.Any(rule =>
                    rule.IdentityReference is SecurityIdentifier sid
                    && string.Equals(
                        sid.Value,
                        serviceAccountSid.Value,
                        StringComparison.Ordinal)
                    && (rule.RegistryRights & RegistryRights.ReadKey)
                    == RegistryRights.ReadKey))
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' is not readable by its exact service account.");
            }

            foreach (var sid in fullControlSids)
            {
                if (!rules.Any(rule =>
                        rule.IdentityReference is SecurityIdentifier identity
                        && string.Equals(identity.Value, sid, StringComparison.Ordinal)
                        && (rule.RegistryRights & RegistryRights.FullControl)
                        == RegistryRights.FullControl))
                {
                    throw new InvalidOperationException(
                        $"EventLog source '{serviceName}' lacks FullControl for privileged SID '{sid}'.");
                }
            }

            var serviceRules = rules.Where(rule =>
                    rule.IdentityReference is SecurityIdentifier sid
                    && string.Equals(
                        sid.Value,
                        serviceAccountSid.Value,
                        StringComparison.Ordinal))
                .ToArray();
            if (serviceRules.Length != 1
                || serviceRules[0].RegistryRights != RegistryRights.ReadKey)
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' service-account ACL must be exactly one ReadKey rule.");
            }
        }

        private static void DeleteEventLogSource(
            string serviceName,
            SecurityIdentifier serviceAccountSid)
        {
            if (EventLog.SourceExists(serviceName))
            {
                var currentSid = WindowsIdentity.GetCurrent().User
                                 ?? throw new InvalidOperationException(
                                     "The staged E2E cleanup identity has no SID for EventLog source validation.");
                VerifyEventLogSource(
                    serviceName,
                    serviceAccountSid,
                    currentSid);
                EventLog.DeleteEventSource(serviceName);
            }

            VerifyEventLogSourceAbsent(serviceName);
        }

        private static void VerifyEventLogSourceAbsent(string serviceName)
        {
            if (EventLog.SourceExists(serviceName))
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' is still registered after cleanup.");
            }

            using var key = Registry.LocalMachine.OpenSubKey(
                EventLogSourceRegistryPrefix + serviceName,
                writable: false);
            if (key is not null)
            {
                throw new InvalidOperationException(
                    $"EventLog source '{serviceName}' still exists after cleanup.");
            }
        }

        private static void DeleteEnvironmentValue(string serviceName)
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ServiceRegistryPrefix + serviceName,
                writable: true);
            if (key is null)
            {
                return;
            }

            key.DeleteValue("Environment", throwOnMissingValue: false);
            key.Flush();
            if (key.GetValue("Environment", null) is not null)
            {
                throw new InvalidOperationException(
                    $"Sensitive staged Agent environment persisted after deletion for service '{serviceName}'.");
            }
        }

        private static void DeleteServiceRequired(
            SafeServiceHandle service,
            string serviceName)
        {
            if (!DeleteService(service))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceMarkedForDelete)
                {
                    throw new Win32Exception(
                        error,
                        $"Could not delete staged Agent service '{serviceName}'.");
                }
            }
        }

        private static void WaitForServiceDeletion(
            SafeServiceHandle serviceControlManager,
            string serviceName,
            TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < timeout)
            {
                var service = OpenService(
                    serviceControlManager,
                    serviceName,
                    ServiceQueryStatus);
                if (service.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    service.Dispose();
                    if (error == ErrorServiceDoesNotExist)
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(
                            ServiceRegistryPrefix + serviceName,
                            writable: false);
                        if (key is null)
                        {
                            return;
                        }
                    }
                    else if (error != ErrorServiceMarkedForDelete)
                    {
                        throw new Win32Exception(
                            error,
                            $"Could not prove deletion of staged Agent service '{serviceName}'.");
                    }
                }
                else
                {
                    service.Dispose();
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }

            throw new TimeoutException(
                $"SCM did not fully delete staged Agent service '{serviceName}' and its registry key within {timeout}.");
        }

        private static string QuoteServiceBinaryPath(string executablePath)
        {
            if (executablePath.Contains('"') || executablePath.Contains('\0'))
            {
                throw new InvalidDataException(
                    "The staged Agent service executable path contains characters that cannot be represented in ImagePath.");
            }

            return $"\"{executablePath}\"";
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeServiceHandle OpenSCManager(
            string? machineName,
            string? databaseName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeServiceHandle CreateService(
            SafeServiceHandle serviceControlManager,
            string serviceName,
            string displayName,
            uint desiredAccess,
            uint serviceType,
            uint startType,
            uint errorControl,
            string binaryPathName,
            string? loadOrderGroup,
            IntPtr tagId,
            string? dependencies,
            string serviceStartName,
            string password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeServiceHandle OpenService(
            SafeServiceHandle serviceControlManager,
            string serviceName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(
            SafeServiceHandle service,
            uint argumentCount,
            IntPtr argumentVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(
            SafeServiceHandle service,
            uint control,
            out ServiceStatus serviceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatusEx(
            SafeServiceHandle service,
            uint infoLevel,
            ref ServiceStatusProcess serviceStatus,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfig(
            SafeServiceHandle service,
            IntPtr queryServiceConfig,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfig2(
            SafeServiceHandle service,
            uint infoLevel,
            IntPtr buffer,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(SafeServiceHandle service);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(
            SafeProcessHandle processHandle,
            uint exitCode);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueryServiceConfiguration
        {
            public uint ServiceType;
            public uint StartType;
            public uint ErrorControl;
            public IntPtr BinaryPathName;
            public IntPtr LoadOrderGroup;
            public uint TagId;
            public IntPtr Dependencies;
            public IntPtr ServiceStartName;
            public IntPtr DisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceFailureActions
        {
            public uint ResetPeriod;
            public IntPtr RebootMessage;
            public IntPtr Command;
            public uint ActionsCount;
            public IntPtr Actions;
        }

        private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeServiceHandle()
                : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle() => CloseServiceHandle(handle);

            [DllImport("advapi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseServiceHandle(IntPtr serviceHandle);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsAgentProcess : IDisposable
    {
        private const uint ProcessTerminate = 0x0001;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint Synchronize = 0x00100000;
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenQuery = 0x0008;
        private const uint GroupEnabled = 0x00000004;
        private const uint GroupUseForDenyOnly = 0x00000010;
        private const int ErrorInsufficientBuffer = 122;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private const uint WaitFailed = uint.MaxValue;

        private readonly WindowsAgentService _service;
        private readonly SafeProcessHandle _processHandle;
        private readonly uint _processId;
        private int? _exitCode;
        private bool _disposed;

        private WindowsAgentProcess(
            WindowsAgentService service,
            SafeProcessHandle processHandle,
            uint processId,
            AgentHostTokenEvidence tokenEvidence,
            string executablePath,
            string executableSha256)
        {
            _service = service;
            _processHandle = processHandle;
            _processId = processId;
            TokenEvidence = tokenEvidence;
            ExecutablePath = executablePath;
            ExecutableSha256 = executableSha256;
        }

        public int Id => checked((int)_processId);

        public bool HasExited => _exitCode.HasValue || IsExited(_processHandle);

        public int ExitCode => _exitCode
            ?? (HasExited
            ? ReadExitCode(_processHandle)
            : throw new InvalidOperationException(
                "Staged Agent PID " + _processId + " is still running."));

        public AgentHostTokenEvidence TokenEvidence { get; }

        public string ExecutablePath { get; }

        public string ExecutableSha256 { get; }

        internal static SafeProcessHandle OpenRequiredProcess(uint processId)
        {
            if (processId == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processId),
                    "A staged Agent service process ID must be positive.");
            }

            var processHandle = OpenProcess(
                ProcessTerminate | ProcessQueryLimitedInformation | Synchronize,
                inheritHandle: false,
                processId);
            if (processHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                processHandle.Dispose();
                throw new Win32Exception(
                    error,
                    "Could not open staged Agent service PID " + processId + ".");
            }

            return processHandle;
        }

        internal static WindowsAgentProcess CreateValidated(
            WindowsAgentService service,
            SafeProcessHandle processHandle,
            uint processId,
            string expectedExecutablePath,
            string expectedExecutableSha256,
            RestrictedAgentIdentity requestedIdentity)
        {
            ArgumentNullException.ThrowIfNull(service);
            ArgumentNullException.ThrowIfNull(processHandle);
            if (processHandle.IsInvalid || processHandle.IsClosed)
            {
                throw new ArgumentException(
                    "The staged Agent service process handle is unavailable.",
                    nameof(processHandle));
            }

            ArgumentNullException.ThrowIfNull(requestedIdentity);
            var validation = ValidateRequiredProcess(
                processHandle,
                processId,
                expectedExecutablePath,
                expectedExecutableSha256,
                requestedIdentity.Sid);
            return new WindowsAgentProcess(
                service,
                processHandle,
                processId,
                validation.TokenEvidence,
                validation.ExecutablePath,
                validation.ExecutableSha256);
        }

        internal static ValidatedProcessEvidence ValidateRequiredProcess(
            SafeProcessHandle processHandle,
            uint processId,
            string expectedExecutablePath,
            string expectedExecutableSha256,
            string expectedSid)
        {
            ArgumentNullException.ThrowIfNull(processHandle);
            if (processHandle.IsInvalid || processHandle.IsClosed)
            {
                throw new ArgumentException(
                    "The staged Agent service process handle is unavailable.",
                    nameof(processHandle));
            }

            var tokenEvidence = ReadRequiredTokenEvidence(processHandle, expectedSid);
            var actualExecutablePath = ReadRequiredExecutablePath(processHandle);
            var fullExpectedPath = Path.GetFullPath(expectedExecutablePath);
            if (!string.Equals(
                    actualExecutablePath,
                    fullExpectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Staged Agent service PID " + processId
                    + " runs image '" + actualExecutablePath
                    + "', not frozen image '" + fullExpectedPath + "'.");
            }

            var actualSha256 = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(actualExecutablePath)));
            if (!string.Equals(
                    actualSha256,
                    expectedExecutableSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Staged Agent service PID " + processId
                    + " image hash differs from the hash captured before SCM installation.");
            }

            return new ValidatedProcessEvidence(
                tokenEvidence,
                actualExecutablePath,
                actualSha256);
        }

        public async Task<int> StopCleanlyAsync(TimeSpan timeout)
        {
            ThrowIfDisposed();
            var exitCode = await Task.Run(
                () => _service.StopCleanly(_processId, _processHandle, timeout));
            _exitCode = exitCode;
            return exitCode;
        }

        public void Kill()
        {
            if (_disposed)
            {
                return;
            }

            if (_exitCode.HasValue)
            {
                return;
            }

            _exitCode = _service.Kill(_processId, _processHandle);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        internal static bool IsExited(SafeProcessHandle processHandle) =>
            WaitForExit(processHandle, TimeSpan.Zero);

        internal static int ReadExitCode(SafeProcessHandle processHandle)
        {
            if (!IsExited(processHandle))
            {
                throw new InvalidOperationException(
                    "The staged Agent service process is still running.");
            }

            if (!GetExitCodeProcess(processHandle, out var exitCode))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read the staged Agent service process exit code.");
            }

            return unchecked((int)exitCode);
        }

        internal static void WaitForExitRequired(
            SafeProcessHandle processHandle,
            TimeSpan timeout,
            uint processId)
        {
            if (!WaitForExit(processHandle, timeout))
            {
                throw new TimeoutException(
                    "Staged Agent service PID " + processId
                    + " did not exit within " + timeout + ".");
            }
        }

        private static bool WaitForExit(
            SafeProcessHandle processHandle,
            TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero
                || timeout.TotalMilliseconds > uint.MaxValue - 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    "The staged Agent process wait timeout must be non-negative and representable by Win32.");
            }

            var milliseconds = checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
            return WaitForSingleObject(processHandle, milliseconds) switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                WaitFailed => throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not wait for the staged Agent service process handle."),
                var result => throw new InvalidOperationException(
                    "WaitForSingleObject returned unexpected result 0x"
                    + result.ToString("x8", CultureInfo.InvariantCulture)
                    + " for the staged Agent service process.")
            };
        }

        private static string ReadRequiredExecutablePath(
            SafeProcessHandle processHandle)
        {
            const int maximumWindowsPathLength = 32_768;
            var executablePath = new StringBuilder(maximumWindowsPathLength);
            var executablePathLength = checked((uint)executablePath.Capacity);
            if (!QueryFullProcessImageName(
                    processHandle,
                    flags: 0,
                    executablePath,
                    ref executablePathLength))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not inspect the staged Agent service process image path.");
            }

            if (executablePathLength == 0)
            {
                throw new InvalidOperationException(
                    "The staged Agent service process image path is empty.");
            }

            return Path.GetFullPath(
                executablePath.ToString(0, checked((int)executablePathLength)));
        }

        private static AgentHostTokenEvidence ReadRequiredTokenEvidence(
            SafeProcessHandle processHandle,
            string expectedSid)
        {
            var evidence = ReadTokenEvidence(processHandle);
            if (!string.Equals(
                    evidence.UserSid,
                    expectedSid,
                    StringComparison.Ordinal)
                || !evidence.NonAdministrative)
            {
                throw new InvalidOperationException(
                    "The staged Agent service token did not prove the exact authenticated, "
                    + "primary, non-elevated, non-administrative service identity. "
                    + JsonSerializer.Serialize(evidence));
            }

            return evidence;
        }

        private static AgentHostTokenEvidence ReadTokenEvidence(
            SafeProcessHandle processHandle)
        {
            if (!OpenProcessTokenSafe(
                    processHandle,
                    TokenQuery | TokenDuplicate,
                    out var token))
            {
                var tokenError = Marshal.GetLastWin32Error();
                token.Dispose();
                throw new Win32Exception(
                    tokenError,
                    "Could not open the staged Agent service process token.");
            }

            using (token)
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                var userSid = identity.User
                              ?? throw new InvalidOperationException(
                                  "The staged Agent service process token has no user SID.");
                var administratorSid = new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null);
                var groups = ReadTokenGroups(token.DangerousGetHandle());
                var administrator = groups.FirstOrDefault(group =>
                    string.Equals(
                        group.Sid,
                        administratorSid.Value,
                        StringComparison.Ordinal));
                var administratorPresent = administrator is not null;
                var administratorEnabled = administrator is not null
                                           && (administrator.Attributes & GroupEnabled) != 0
                                           && (administrator.Attributes & GroupUseForDenyOnly) == 0;
                var administratorDenyOnly = administrator is not null
                                            && (administrator.Attributes & GroupUseForDenyOnly) != 0;
                var principalAdministrator = ReadRequiredEnabledTokenMembership(
                    token,
                    administratorSid);
                return new AgentHostTokenEvidence(
                    identity.Name,
                    userSid.Value,
                    ReadTokenInt32(
                        token.DangerousGetHandle(),
                        TokenInformationClass.TokenType) == 1,
                    ReadTokenInt32(
                        token.DangerousGetHandle(),
                        TokenInformationClass.TokenElevation) != 0,
                    ReadTokenInt32(
                        token.DangerousGetHandle(),
                        TokenInformationClass.TokenHasRestrictions) != 0,
                    administratorPresent,
                    administratorEnabled,
                    administratorDenyOnly,
                    principalAdministrator,
                    !string.IsNullOrWhiteSpace(identity.AuthenticationType)
                    && !userSid.IsWellKnown(WellKnownSidType.AnonymousSid),
                    userSid.IsWellKnown(WellKnownSidType.LocalSystemSid));
            }
        }

        private static bool ReadRequiredEnabledTokenMembership(
            SafeAccessTokenHandle primaryToken,
            SecurityIdentifier sid)
        {
            if (!DuplicateToken(
                    primaryToken,
                    SecurityImpersonationLevel.SecurityIdentification,
                    out var impersonationToken))
            {
                var error = Marshal.GetLastWin32Error();
                impersonationToken.Dispose();
                throw new Win32Exception(
                    error,
                    "Could not duplicate the staged Agent primary token for an independent administrator-membership proof.");
            }

            using (impersonationToken)
            {
                var sidBytes = new byte[sid.BinaryLength];
                sid.GetBinaryForm(sidBytes, 0);
                var pinnedSid = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
                try
                {
                    if (!CheckTokenMembership(
                            impersonationToken,
                            pinnedSid.AddrOfPinnedObject(),
                            out var isMember))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not independently inspect staged Agent administrator membership on its duplicated impersonation token.");
                    }

                    return isMember;
                }
                finally
                {
                    pinnedSid.Free();
                }
            }
        }

        private static int ReadTokenInt32(
            IntPtr token,
            TokenInformationClass informationClass)
        {
            var value = 0;
            if (!GetTokenInformation(
                    token,
                    informationClass,
                    out value,
                    sizeof(int),
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not inspect staged Agent token information "
                    + informationClass + ".");
            }

            return value;
        }

        private static List<TokenGroupEvidence> ReadTokenGroups(IntPtr token)
        {
            _ = GetTokenInformation(
                token,
                TokenInformationClass.TokenGroups,
                IntPtr.Zero,
                0,
                out var requiredLength);
            var sizingError = Marshal.GetLastWin32Error();
            if (requiredLength <= 0 || sizingError != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(
                    sizingError,
                    "Could not size the staged Agent token groups buffer.");
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(
                        token,
                        TokenInformationClass.TokenGroups,
                        buffer,
                        requiredLength,
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not inspect the staged Agent token groups.");
                }

                var count = checked((uint)Marshal.ReadInt32(buffer));
                var offset = Marshal.OffsetOf<TokenGroupsHeader>(
                    nameof(TokenGroupsHeader.FirstGroup)).ToInt32();
                var stride = Marshal.SizeOf<TokenSidAndAttributes>();
                var groups = new List<TokenGroupEvidence>(checked((int)count));
                for (var index = 0u; index < count; index++)
                {
                    var group = Marshal.PtrToStructure<TokenSidAndAttributes>(
                        IntPtr.Add(buffer, checked(offset + (int)index * stride)));
                    if (group.Sid != IntPtr.Zero)
                    {
                        groups.Add(new TokenGroupEvidence(
                            new SecurityIdentifier(group.Sid).Value,
                            group.Attributes));
                    }
                }

                return groups;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        internal sealed record ValidatedProcessEvidence(
            AgentHostTokenEvidence TokenEvidence,
            string ExecutablePath,
            string ExecutableSha256);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport(
            "advapi32.dll",
            EntryPoint = "OpenProcessToken",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessTokenSafe(
            SafeProcessHandle processHandle,
            uint desiredAccess,
            out SafeAccessTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateToken(
            SafeAccessTokenHandle existingTokenHandle,
            SecurityImpersonationLevel impersonationLevel,
            out SafeAccessTokenHandle duplicateTokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CheckTokenMembership(
            SafeAccessTokenHandle tokenHandle,
            IntPtr sidToCheck,
            [MarshalAs(UnmanagedType.Bool)] out bool isMember);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            SafeProcessHandle handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(
            SafeProcessHandle processHandle,
            out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage(
            "Performance",
            "CA1838:Avoid StringBuilder parameters for P/Invokes",
            Justification = "QueryFullProcessImageNameW writes the image path into a caller-owned buffer.")]
        private static extern bool QueryFullProcessImageName(
            SafeProcessHandle processHandle,
            uint flags,
            StringBuilder executablePath,
            ref uint executablePathLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            TokenInformationClass tokenInformationClass,
            out int tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            TokenInformationClass tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenGroupsHeader
        {
            public uint GroupCount;
            public TokenSidAndAttributes FirstGroup;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenSidAndAttributes
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        private enum TokenInformationClass
        {
            TokenUser = 1,
            TokenGroups = 2,
            TokenType = 8,
            TokenElevation = 20,
            TokenHasRestrictions = 21
        }

        private enum SecurityImpersonationLevel
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

        private sealed record TokenGroupEvidence(string Sid, uint Attributes);
    }
}
