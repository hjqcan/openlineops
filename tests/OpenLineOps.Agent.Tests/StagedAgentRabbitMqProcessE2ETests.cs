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
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.ContentProtection;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Processes.Application.FlowIr;
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

        var suffix = Guid.NewGuid().ToString("N");
        var agentId = $"agent.rabbitmq-e2e.{suffix}";
        var stationId = $"station.rabbitmq-e2e.{suffix}";
        var coordinatorId = $"coordinator-rabbitmq-e2e-{suffix}";
        var agentIdentity = RestrictedAgentIdentity.CreateRequired(suffix);
        var root = Path.Combine(
            agentIdentity.RequiresSharedTestRoot
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Temp")
                : Path.GetTempPath(),
            $"olo-staged-agent-rmq-{suffix}");
        var dataRoot = Path.Combine(root, "agent-data");
        var distributionRoot = Path.Combine(root, "package-distribution");
        var runtimeWorkRoot = Path.Combine(root, "runtime-work");
        var packageCacheRoot = Path.Combine(root, "package-cache");
        StagedCoordinatorApiProcess? artifactApi = null;
        HttpClient? operatorTraceClient = null;
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
            Directory.CreateDirectory(root);
            agentIdentity.GrantDirectoryAccess(
                root,
                FileSystemRights.Modify | FileSystemRights.Synchronize);
            agentIdentity.GrantDirectoryAccess(
                prerequisites.AgentBundleRoot,
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
                prerequisites,
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
            agent = WindowsAgentProcess.Start(
                Path.Combine(prerequisites.AgentBundleRoot, "OpenLineOps.Agent.exe"),
                prerequisites.AgentBundleRoot,
                environment,
                agentIdentity);
            initialAgentTokenEvidence = agent.TokenEvidence;
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

            var firstAgentPid = agent.Id;
            var firstExitCode = await agent.StopCleanlyAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, firstExitCode);
            agent.Dispose();
            agent = null;

            agent = WindowsAgentProcess.Start(
                Path.Combine(prerequisites.AgentBundleRoot, "OpenLineOps.Agent.exe"),
                prerequisites.AgentBundleRoot,
                environment,
                agentIdentity);
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

            var restartExitCode = await agent.StopCleanlyAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, restartExitCode);
            agent.Dispose();
            agent = null;
            stationStore.Dispose();
            stationStore = null;

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
            CaptureCleanupFailure(
                cleanupFailures,
                () => DeleteWorkRoot(root, packageCacheRoot));
            CaptureCleanupFailure(
                cleanupFailures,
                agentIdentity.Dispose);
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
        var environment = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => (string?)entry.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        foreach (var key in environment.Keys
                     .Where(key => key.StartsWith(
                         "OpenLineOps__Agent__",
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            environment.Remove(key);
        }

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
            && !AdministratorGroupEnabled
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
        private const string TemporaryStandardAccountStrategy =
            "temporary-standard-account";
        private const string InheritedUacFilteredTokenStrategy =
            "inherited-uac-filtered-token";

        private readonly List<GrantedDirectoryAccess> _grantedAccess = [];
        private readonly string? _userName;
        private readonly string? _password;
        private bool _disposed;

        private RestrictedAgentIdentity(
            string accountName,
            string sid,
            string? userName,
            string? password,
            string strategy)
        {
            AccountName = accountName;
            Sid = sid;
            _userName = userName;
            _password = password;
            Strategy = strategy;
        }

        public string AccountName { get; }

        public string Sid { get; }

        public string Strategy { get; }

        public bool RequiresSharedTestRoot => _userName is not null;

        internal string? UserName => _userName;

        internal string? Password => _password;

        internal string? Domain => _userName is null ? null : Environment.MachineName;

        public static RestrictedAgentIdentity CreateRequired(
            string suffix,
            bool allowInheritedUacFilteredIdentity = true)
        {
            var userName = $"oloe2e{suffix[..10]}";
            var password = $"Aa1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}";
            var user = new UserInfo1
            {
                Name = userName,
                Password = password,
                Privilege = UserPrivilegeUser,
                Flags = UserFlagScript | UserFlagNormalAccount | UserFlagPasswordDoesNotExpire,
                Comment = "Temporary OpenLineOps staged Agent E2E identity"
            };
            var status = NetUserAdd(null, 1, ref user, out var parameterError);
            if (status == AccessDenied)
            {
                if (!allowInheritedUacFilteredIdentity)
                {
                    throw new InvalidOperationException(
                        "The formal two-Agent production gate requires permission to provision "
                        + "two distinct temporary standard Windows accounts; NetUserAdd was denied.");
                }

                using var currentIdentity = WindowsIdentity.GetCurrent(
                    TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
                var currentSid = currentIdentity.User
                                 ?? throw new InvalidOperationException(
                                     "The staged Agent E2E Windows identity has no SID.");
                var currentToken = WindowsAgentProcess.ReadCurrentProcessTokenEvidence();
                if (!string.Equals(
                        currentToken.UserSid,
                        currentSid.Value,
                        StringComparison.Ordinal)
                    || !currentToken.NonAdministrative
                    || !currentToken.AdministratorGroupPresent
                    || !currentToken.AdministratorGroupDenyOnly)
                {
                    throw new InvalidOperationException(
                        "NetUserAdd was denied and the current process token did not prove "
                        + "an authenticated, primary, non-elevated UAC-filtered identity "
                        + "with the Administrators group deny-only. "
                        + JsonSerializer.Serialize(currentToken));
                }

                return new RestrictedAgentIdentity(
                    currentIdentity.Name,
                    currentSid.Value,
                    null,
                    null,
                    InheritedUacFilteredTokenStrategy);
            }

            if (status != Success)
            {
                throw new InvalidOperationException(
                    $"Could not provision the staged Agent standard account (NetUserAdd {status}, parameter {parameterError}).");
            }

            try
            {
                var sid = (SecurityIdentifier)new NTAccount(
                        Environment.MachineName,
                        userName)
                    .Translate(typeof(SecurityIdentifier));
                AddToBuiltinUsers(sid);
                return new RestrictedAgentIdentity(
                    $"{Environment.MachineName}\\{userName}",
                    sid.Value,
                    userName,
                    password,
                    TemporaryStandardAccountStrategy);
            }
            catch
            {
                _ = NetUserDel(null, userName);
                throw;
            }
        }

        public void GrantDirectoryAccess(
            string path,
            FileSystemRights rights,
            InheritanceFlags inheritanceFlags =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
        {
            if (_userName is null)
            {
                return;
            }

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

            _disposed = true;
            var failures = new List<Exception>();
            if (_userName is not null)
            {
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

                CaptureCleanupFailure(
                    failures,
                    () =>
                    {
                        if (DeleteProfile(Sid, null, null))
                        {
                            return;
                        }

                        var error = Marshal.GetLastWin32Error();
                        if (error is not 2 and not 3)
                        {
                            throw new Win32Exception(
                                error,
                                $"Could not delete the temporary staged Agent profile for SID {Sid}.");
                        }
                    });
                CaptureCleanupFailure(
                    failures,
                    () =>
                    {
                        var status = NetUserDel(null, _userName);
                        if (status is not Success and not UserNotFound)
                        {
                            throw new InvalidOperationException(
                                $"Could not delete the temporary staged Agent account '{_userName}' (NetUserDel {status}).");
                        }
                    });
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "One or more staged Agent identity cleanup steps failed.",
                    failures);
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

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserAdd(
            string? serverName,
            uint level,
            ref UserInfo1 buffer,
            out uint parameterError);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserDel(string? serverName, string userName);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetLocalGroupAddMembers(
            string? serverName,
            string groupName,
            uint level,
            ref LocalGroupMembersInfo0 buffer,
            uint totalEntries);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteProfile(
            string sidString,
            string? profilePath,
            string? computerName);

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

        private sealed record GrantedDirectoryAccess(
            string Path,
            FileSystemAccessRule Rule);
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
    private sealed class WindowsAgentProcess : IDisposable
    {
        private const uint CreateNewProcessGroup = 0x00000200;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint LogonWithProfile = 0x00000001;
        private const uint TokenQuery = 0x0008;
        private const uint TokenDuplicate = 0x0002;
        private const uint GroupEnabled = 0x00000004;
        private const uint GroupUseForDenyOnly = 0x00000010;
        private const int ErrorInsufficientBuffer = 122;
        private const uint CtrlBreakEvent = 1;

        private readonly Process _process;
        private readonly bool _ownsConsole;

        private WindowsAgentProcess(
            Process process,
            bool ownsConsole,
            AgentHostTokenEvidence tokenEvidence,
            string executablePath,
            string executableSha256)
        {
            _process = process;
            _ownsConsole = ownsConsole;
            TokenEvidence = tokenEvidence;
            ExecutablePath = executablePath;
            ExecutableSha256 = executableSha256;
        }

        public int Id => _process.Id;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public AgentHostTokenEvidence TokenEvidence { get; }

        public string ExecutablePath { get; }

        public string ExecutableSha256 { get; }

        public static WindowsAgentProcess Start(
            string executablePath,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            RestrictedAgentIdentity identity)
        {
            var ownsConsole = EnsureConsole();
            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>()
            };
            var commandLine = new StringBuilder($"\"{executablePath}\"");
            var environmentBlock = BuildEnvironmentBlock(environment);
            var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
            try
            {
                bool created;
                ProcessInformation processInformation;
                if (identity.UserName is not null)
                {
                    created = CreateProcessWithLogon(
                        identity.UserName,
                        identity.Domain,
                        identity.Password
                        ?? throw new InvalidOperationException(
                            "The staged Agent standard account has no password."),
                        LogonWithProfile,
                        executablePath,
                        commandLine,
                        CreateNewProcessGroup | CreateUnicodeEnvironment,
                        environmentPointer,
                        workingDirectory,
                        ref startupInfo,
                        out processInformation);
                }
                else
                {
                    created = CreateProcess(
                        executablePath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        inheritHandles: false,
                        CreateNewProcessGroup | CreateUnicodeEnvironment,
                        environmentPointer,
                        workingDirectory,
                        ref startupInfo,
                        out processInformation);
                }

                if (!created)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not start staged Agent '{executablePath}' as '{identity.AccountName}'.");
                }

                try
                {
                    var process = Process.GetProcessById(
                        checked((int)processInformation.ProcessId));
                    try
                    {
                        var tokenEvidence = ReadRequiredTokenEvidence(
                            process,
                            identity);
                        var actualExecutablePath = ReadRequiredExecutablePath(
                            processInformation.Process);
                        var requestedExecutablePath = Path.GetFullPath(executablePath);
                        if (!string.Equals(
                                actualExecutablePath,
                                requestedExecutablePath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "The staged Agent process main module differs from the requested executable.");
                        }

                        var executableSha256 = Convert.ToHexStringLower(
                            SHA256.HashData(File.ReadAllBytes(actualExecutablePath)));
                        return new WindowsAgentProcess(
                            process,
                            ownsConsole,
                            tokenEvidence,
                            actualExecutablePath,
                            executableSha256);
                    }
                    catch
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(10_000);
                        }

                        process.Dispose();
                        throw;
                    }
                }
                finally
                {
                    _ = CloseHandle(processInformation.Thread);
                    _ = CloseHandle(processInformation.Process);
                }
            }
            catch
            {
                if (ownsConsole)
                {
                    _ = FreeConsole();
                }

                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(environmentPointer);
            }
        }

        private static string ReadRequiredExecutablePath(IntPtr processHandle)
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
                    "Could not inspect the staged Agent process image path.");
            }

            if (executablePathLength == 0)
            {
                throw new InvalidOperationException(
                    "The staged Agent process image path is empty.");
            }

            return Path.GetFullPath(
                executablePath.ToString(0, checked((int)executablePathLength)));
        }

        public async Task<int> StopCleanlyAsync(TimeSpan timeout)
        {
            if (_process.HasExited)
            {
                return _process.ExitCode;
            }

            if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, checked((uint)_process.Id)))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not signal Ctrl+Break to the staged Agent process group.");
            }

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(timeoutCancellation.Token);
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The staged Agent did not stop cleanly after Ctrl+Break.");
            }

            return _process.ExitCode;
        }

        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }

        public void Dispose()
        {
            _process.Dispose();
            if (_ownsConsole)
            {
                _ = FreeConsole();
            }
        }

        internal static AgentHostTokenEvidence ReadCurrentProcessTokenEvidence()
        {
            using var process = Process.GetCurrentProcess();
            return ReadTokenEvidence(process);
        }

        private static AgentHostTokenEvidence ReadRequiredTokenEvidence(
            Process process,
            RestrictedAgentIdentity requestedIdentity)
        {
            var evidence = ReadTokenEvidence(process);
            if (!string.Equals(
                    evidence.UserSid,
                    requestedIdentity.Sid,
                    StringComparison.Ordinal)
                || !evidence.NonAdministrative
                || (evidence.AdministratorGroupPresent
                    && !evidence.AdministratorGroupDenyOnly))
            {
                throw new InvalidOperationException(
                    "The staged Agent child token did not prove the required authenticated, "
                    + "primary, non-elevated, non-administrative identity. "
                    + JsonSerializer.Serialize(evidence));
            }

            return evidence;
        }

        private static AgentHostTokenEvidence ReadTokenEvidence(Process process)
        {
            if (!OpenProcessToken(
                    process.Handle,
                    TokenQuery | TokenDuplicate,
                    out var token))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not open staged Agent PID {process.Id} for token verification.");
            }

            try
            {
                using var identity = new WindowsIdentity(token);
                var userSid = identity.User
                              ?? throw new InvalidOperationException(
                                  "The staged Agent child token has no user SID.");
                var administratorSid = new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null);
                var groups = ReadTokenGroups(token);
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
                var principalAdministrator = new WindowsPrincipal(identity)
                    .IsInRole(WindowsBuiltInRole.Administrator);
                var evidence = new AgentHostTokenEvidence(
                    identity.Name,
                    userSid.Value,
                    ReadTokenInt32(token, TokenInformationClass.TokenType) == 1,
                    ReadTokenInt32(token, TokenInformationClass.TokenElevation) != 0,
                    ReadTokenInt32(token, TokenInformationClass.TokenHasRestrictions) != 0,
                    administratorPresent,
                    administratorEnabled,
                    administratorDenyOnly,
                    principalAdministrator,
                    !string.IsNullOrWhiteSpace(identity.AuthenticationType)
                    && !userSid.IsWellKnown(WellKnownSidType.AnonymousSid),
                    userSid.IsWellKnown(WellKnownSidType.LocalSystemSid));
                return evidence;
            }
            finally
            {
                _ = CloseHandle(token);
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
                    $"Could not inspect staged Agent token information {informationClass}.");
            }

            return value;
        }

        private static List<TokenGroupEvidence> ReadTokenGroups(
            IntPtr token)
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

        private static bool EnsureConsole()
        {
            var processes = new uint[1];
            if (GetConsoleProcessList(processes, 1) != 0)
            {
                return false;
            }

            if (!AllocConsole())
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not allocate a console for staged Agent shutdown control.");
            }

            return true;
        }

        private static string BuildEnvironmentBlock(
            IReadOnlyDictionary<string, string> environment)
        {
            var builder = new StringBuilder();
            foreach (var (key, value) in environment.OrderBy(
                         pair => pair.Key,
                         StringComparer.OrdinalIgnoreCase))
            {
                if (key.Contains('=') || key.Contains('\0') || value.Contains('\0'))
                {
                    throw new InvalidDataException(
                        $"Environment entry '{key}' cannot be represented in a Windows block.");
                }

                builder.Append(key).Append('=').Append(value).Append('\0');
            }

            builder.Append('\0');
            return builder.ToString();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage(
            "Performance",
            "CA1838:Avoid StringBuilder parameters for P/Invokes",
            Justification = "CreateProcessW requires a writable null-terminated command-line buffer.")]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage(
            "Performance",
            "CA1838:Avoid StringBuilder parameters for P/Invokes",
            Justification = "CreateProcessWithLogonW requires a writable null-terminated command-line buffer.")]
        private static extern bool CreateProcessWithLogon(
            string userName,
            string? domain,
            string password,
            uint logonFlags,
            string applicationName,
            StringBuilder commandLine,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleProcessList(
            [Out] uint[] processList,
            uint processCount);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage(
            "Performance",
            "CA1838:Avoid StringBuilder parameters for P/Invokes",
            Justification = "QueryFullProcessImageNameW writes the image path into a caller-owned buffer.")]
        private static extern bool QueryFullProcessImageName(
            IntPtr processHandle,
            uint flags,
            StringBuilder executablePath,
            ref uint executablePathLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

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

        private sealed record TokenGroupEvidence(string Sid, uint Attributes);
    }
}
